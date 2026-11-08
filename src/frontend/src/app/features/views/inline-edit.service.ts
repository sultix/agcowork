import {
  DestroyRef,
  EnvironmentInjector,
  Injectable,
  computed,
  createEnvironmentInjector,
  inject,
  signal,
} from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { ApiClient } from '../../core/api/api-client';
import { CellEditor, CellState, TITLE_KEY, withValue } from '../../shared/table/inline-edit';
import { describeError } from '../../shared/ui/api-error-text';
import { RecordDto, RecordSaveService, RevisionConflict } from '../records/record-save.service';

/** Where the edited records live (the table's or board's loaded rows). */
export interface RowStore {
  find(id: string): RecordDto | undefined;
  replace(record: RecordDto): void;
}

/** The open 409 of one cell, resolved through the conflict dialog of the document view. */
export interface CellConflict {
  readonly recordId: string;
  readonly key: string;
  readonly conflict: RevisionConflict;
}

interface SaverEntry {
  readonly injector: EnvironmentInjector;
  readonly saver: RecordSaveService;
  /** Last known server state of the record (what the next save is based on). */
  server: RecordDto;
  /** Values shown but not yet saved, by key (queued or running edits). */
  readonly pending: Map<string, unknown>;
  /** Edits of one record run one after the other. */
  queue: Promise<unknown>;
  running: number;
}

/**
 * Saves single values of loaded records (table cells, board cards) through the document view's
 * {@link RecordSaveService}: `PATCH` with only the changed key and `expectedRevision`, one save
 * after the other per record, conflicts (409) reported for the conflict dialog. The change is
 * shown at once (optimistic); a rejected value is rolled back and the cell keeps the message and
 * the rejected value. Provide it on the component that hosts the rows and call {@link bind} once.
 */
@Injectable()
export class InlineEditService implements CellEditor {
  private readonly api = inject(ApiClient);
  private readonly environment = inject(EnvironmentInjector);

  private slug: () => string | null = () => null;
  private rows: RowStore = { find: () => undefined, replace: () => undefined };
  private readonly savers = new Map<string, SaverEntry>();

  /** Status of the cells that are saving or failed, keyed `recordId:key`. */
  readonly cells = signal<ReadonlyMap<string, CellState>>(new Map());
  readonly conflict = signal<CellConflict | null>(null);
  readonly resolving = signal(false);

  /** Saves queued or running, across records. */
  private readonly activeSaves = signal(0);
  /** Counts saves started; a reload fetched before a save must not be applied after it. */
  readonly generation = signal(0);
  /**
   * True while a save is queued or running or a conflict waits for the dialog: the rows hold
   * values the server does not have yet, so they must not be replaced by a reload.
   */
  readonly busy = computed(
    () => this.activeSaves() > 0 || this.conflict() !== null || this.resolving(),
  );

  constructor() {
    inject(DestroyRef).onDestroy(() => {
      for (const id of [...this.savers.keys()]) {
        this.drop(id);
      }
    });
  }

  bind(slug: () => string | null, rows: RowStore): void {
    this.slug = slug;
    this.rows = rows;
  }

  cell(recordId: string, key: string): CellState | undefined {
    return this.cells().get(cellKey(recordId, key));
  }

  /**
   * Saves `value` as the new `key` of the record. Resolves `true` when saved. On failure the row
   * goes back to the server value, except for a conflict without `rollback` (the table keeps the
   * user's value until the dialog decides). The board passes `rollback: true`.
   */
  save(
    record: RecordDto,
    key: string,
    value: unknown,
    options: { rollback?: boolean } = {},
  ): Promise<boolean> {
    const slug = this.slug();
    if (!slug) {
      return Promise.resolve(false);
    }
    const row = this.rows.find(record.id) ?? record;
    const entry = this.entryFor(row);
    const pendingValue = value ?? null;
    entry.pending.set(key, pendingValue);
    entry.running++;
    this.activeSaves.update((n) => n + 1);
    this.generation.update((n) => n + 1);
    this.rows.replace(withValue(row, key, pendingValue));
    this.setCell(record.id, key, { status: 'saving', message: null });

    const run = entry.queue.then(() => this.run(slug, entry, key, pendingValue, options));
    entry.queue = run.catch(() => undefined);
    return run.finally(() => {
      entry.running--;
      this.activeSaves.update((n) => n - 1);
    });
  }

  /** "Neu laden": takes the server state of the conflicting record, dropping the user's values. */
  async reloadConflict(): Promise<void> {
    const open = this.conflict();
    const slug = this.slug();
    if (!open || !slug) {
      return;
    }
    this.resolving.set(true);
    try {
      const fresh = await firstValueFrom(
        this.api.workspace(slug).get('/records/{recordId}', { path: { recordId: open.recordId } }),
      );
      const entry = this.savers.get(open.recordId);
      if (entry) {
        entry.server = fresh;
        entry.pending.clear();
        entry.saver.attach(slug, fresh);
      }
      this.rows.replace(fresh);
      this.clearRecord(open.recordId);
    } catch (error) {
      this.setCell(open.recordId, open.key, { status: 'error', message: describeError(error) });
      this.drop(open.recordId);
    } finally {
      this.conflict.set(null);
      this.resolving.set(false);
    }
  }

  /** "Überschreiben": saves the user's values without `expectedRevision`. */
  async overwriteConflict(): Promise<void> {
    const open = this.conflict();
    const entry = open ? this.savers.get(open.recordId) : undefined;
    if (!open || !entry) {
      this.conflict.set(null);
      return;
    }
    this.resolving.set(true);
    try {
      const saved = await entry.saver.overwrite();
      this.conflict.set(null);
      if (saved) {
        entry.server = this.serverOf(entry.saver);
        entry.pending.clear();
        this.clearRecord(open.recordId);
        this.rows.replace(entry.server);
      } else {
        this.setCell(open.recordId, open.key, {
          status: 'error',
          message: entry.saver.saveError() ?? 'Speichern fehlgeschlagen.',
        });
        entry.pending.clear();
        this.rows.replace(entry.server);
        this.drop(open.recordId);
      }
    } finally {
      this.resolving.set(false);
    }
  }

  /** Forgets idle savers and all cell states (the rows were reloaded). */
  reset(): void {
    for (const [id, entry] of [...this.savers]) {
      if (entry.running === 0) {
        this.drop(id);
      }
    }
    this.cells.set(new Map());
    this.conflict.set(null);
  }

  private async run(
    slug: string,
    entry: SaverEntry,
    key: string,
    value: unknown,
    options: { rollback?: boolean },
  ): Promise<boolean> {
    const { saver } = entry;
    const id = entry.server.id;
    if (!saver.conflict()) {
      saver.attach(slug, entry.server);
    }
    if (key === TITLE_KEY) {
      saver.setTitle(typeof value === 'string' ? value : '');
    } else {
      saver.setProperty(key, value);
    }

    const saved = await saver.save();
    if (entry.pending.get(key) === value) {
      entry.pending.delete(key);
    }

    if (saved) {
      entry.server = this.serverOf(saver);
      this.setCell(id, key, null);
      this.rows.replace(this.shown(entry));
      return true;
    }

    const conflict = saver.conflict();
    if (conflict) {
      this.setCell(id, key, { status: 'conflict', message: 'Von jemand anderem geändert.' });
      if (options.rollback) {
        this.rows.replace(this.shown(entry));
      } else {
        entry.pending.set(key, value); // stays visible until the dialog decides
      }
      this.conflict.set({ recordId: id, key, conflict });
      return false;
    }

    const errors = saver.fieldErrors();
    const message = (errors[key] ?? Object.values(errors).flat()).join(' ');
    // Only this key goes back; edits queued behind it must not resend the rejected value.
    if (key === TITLE_KEY) {
      saver.discardTitleEdit();
    } else {
      saver.discardPropertyEdits([key]);
    }
    const archivedAt = saver.record()?.archivedAt ?? entry.server.archivedAt;
    entry.server = { ...entry.server, archivedAt };
    this.setCell(id, key, {
      status: 'error',
      message: message || saver.saveError() || 'Speichern fehlgeschlagen.',
      draft: value,
    });
    this.rows.replace(this.shown(entry));
    return false;
  }

  /** The entry of a record; an idle one takes the row as its new server state. */
  private entryFor(row: RecordDto): SaverEntry {
    const existing = this.savers.get(row.id);
    if (existing) {
      if (existing.running === 0 && !existing.saver.conflict()) {
        existing.server = row;
        existing.pending.clear();
      }
      return existing;
    }
    const injector = createEnvironmentInjector([RecordSaveService], this.environment);
    const saver = injector.get(RecordSaveService);
    saver.autosave = false;
    const entry: SaverEntry = {
      injector,
      saver,
      server: row,
      pending: new Map(),
      queue: Promise.resolve(),
      running: 0,
    };
    this.savers.set(row.id, entry);
    return entry;
  }

  /** Server state plus the values still waiting to be saved. */
  private shown(entry: SaverEntry): RecordDto {
    let row = entry.server;
    for (const [key, value] of entry.pending) {
      row = withValue(row, key, value);
    }
    return row;
  }

  private serverOf(saver: RecordSaveService): RecordDto {
    return saver.record()!;
  }

  private drop(recordId: string): void {
    this.savers.get(recordId)?.injector.destroy();
    this.savers.delete(recordId);
  }

  private clearRecord(recordId: string): void {
    const prefix = `${recordId}:`;
    this.cells.update((cells) => new Map([...cells].filter(([k]) => !k.startsWith(prefix))));
  }

  private setCell(recordId: string, key: string, state: CellState | null): void {
    this.cells.update((cells) => {
      const next = new Map(cells);
      if (state) {
        next.set(cellKey(recordId, key), state);
      } else {
        next.delete(cellKey(recordId, key));
      }
      return next;
    });
  }
}

function cellKey(recordId: string, key: string): string {
  return `${recordId}:${key}`;
}
