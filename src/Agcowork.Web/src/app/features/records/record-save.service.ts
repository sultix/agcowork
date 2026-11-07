import { DestroyRef, Injectable, computed, inject, signal } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { ApiClient } from '../../core/api/api-client';
import { ApiError } from '../../core/api/api-error';
import { fieldErrorMessage } from '../../core/api/field-errors';
import type { components } from '../../core/api/generated/schema';
import { describeError } from '../../shared/ui/api-error-text';

export type RecordDto = components['schemas']['RecordDto'];
type UpdateRecordBody = components['schemas']['UpdateRecordBody'];

/** Auto-save fires this long after the last change. */
export const AUTOSAVE_DELAY_MS = 2000;

export type PropertyValues = Record<string, unknown>;

/** The record changed on the server since it was loaded (409 `revision_mismatch`). */
export interface RevisionConflict {
  readonly currentRevision: number | null;
  /** `title`, property keys, `body`; `null` when the server could not tell. */
  readonly changedKeys: readonly string[] | null;
}

function sameJson(a: unknown, b: unknown): boolean {
  return JSON.stringify(a ?? null) === JSON.stringify(b ?? null);
}

/** The server trims strings; the local text with a trailing space is still "the same". */
function sameTrimmed(local: unknown, server: unknown): boolean {
  return typeof local === 'string' && typeof server === 'string' && local.trim() === server;
}

/** API field (`title`, `properties.status`, `status`) → key used by the document view. */
function fieldKey(field: string): string {
  return field.startsWith('properties.') ? field.slice('properties.'.length) : field;
}

/**
 * Loads one record and keeps the unsaved edits of the document view: title, property values
 * and body. Saves send only what changed (`PATCH` with `expectedRevision`), explicitly or
 * debounced 2 s after the last change. After a successful save only revision and metadata are
 * taken over from the server; text the user typed meanwhile stays. One instance per document
 * view (provide it on the component).
 */
@Injectable()
export class RecordSaveService {
  private readonly api = inject(ApiClient);

  private slug = '';
  private recordId = '';
  private loadToken = 0;
  private timer: ReturnType<typeof setTimeout> | null = null;
  private inflight: Promise<boolean> | null = null;

  /** Server state of the record (metadata is current, `body` is the loaded one). */
  readonly record = signal<RecordDto | null>(null);
  readonly loading = signal(false);
  readonly loadError = signal<string | null>(null);

  readonly title = signal('');
  readonly properties = signal<PropertyValues>({});
  readonly body = signal('');
  /** Body handed to the editor; changes only when a record is (re)loaded, never after a save. */
  readonly editorValue = signal('');
  /** Counts successful loads; the view re-creates the editor with each one. */
  readonly loadCount = signal(0);

  private readonly titleDirty = signal(false);
  private readonly dirtyKeys = signal<ReadonlySet<string>>(new Set());
  private readonly bodyDirty = signal(false);

  readonly isDirty = computed(
    () => this.titleDirty() || this.dirtyKeys().size > 0 || this.bodyDirty(),
  );
  readonly saving = signal(false);
  readonly savedAt = signal<Date | null>(null);
  readonly conflict = signal<RevisionConflict | null>(null);
  /** Validation messages by property key (`title` for the title). */
  readonly fieldErrors = signal<Readonly<Record<string, readonly string[]>>>({});
  readonly saveError = signal<string | null>(null);
  /**
   * Set when the editor may show an outdated body after "Überschreiben" (fetching the body failed
   * or the user typed on the old text meanwhile). Holds the revision before the overwrite: the
   * next body save sends it as `expectedRevision`, which forces the conflict dialog.
   */
  readonly bodyStale = signal<number | null>(null);

  /** Called before every save to pull text the editor has not reported yet (see document view). */
  beforeSave: (() => void) | null = null;

  constructor() {
    inject(DestroyRef).onDestroy(() => {
      this.loadToken++;
      this.clearTimer();
    });
  }

  /** Loads the record with body and discards all unsaved edits. */
  async load(slug: string, recordId: string): Promise<void> {
    const token = ++this.loadToken;
    this.slug = slug;
    this.recordId = recordId;
    this.clearTimer();
    this.loading.set(true);
    this.loadError.set(null);
    try {
      const record = await firstValueFrom(
        this.api.workspace(slug).get('/records/{recordId}', {
          path: { recordId },
          query: { includeBody: true },
        }),
      );
      if (token !== this.loadToken) {
        return;
      }
      this.record.set(record);
      this.title.set(record.title);
      this.properties.set({ ...record.properties });
      this.body.set(record.body ?? '');
      this.editorValue.set(record.body ?? '');
      this.loadCount.update((n) => n + 1);
      this.titleDirty.set(false);
      this.dirtyKeys.set(new Set());
      this.bodyDirty.set(false);
      this.conflict.set(null);
      this.fieldErrors.set({});
      this.saveError.set(null);
      this.savedAt.set(null);
      this.bodyStale.set(null);
    } catch (error) {
      if (token === this.loadToken) {
        this.record.set(null);
        this.loadError.set(describeError(error));
      }
    } finally {
      if (token === this.loadToken) {
        this.loading.set(false);
      }
    }
  }

  /** Throws away unsaved edits and the conflict, and loads the current server state. */
  reload(): Promise<void> {
    return this.load(this.slug, this.recordId);
  }

  setTitle(title: string): void {
    this.title.set(title);
    this.titleDirty.set(true);
    this.clearFieldError('title');
    this.schedule();
  }

  setProperty(key: string, value: unknown): void {
    this.properties.update((props) => ({ ...props, [key]: value }));
    this.dirtyKeys.update((keys) => new Set(keys).add(key));
    this.clearFieldError(key);
    this.schedule();
  }

  setBody(body: string): void {
    this.body.set(body);
    this.bodyDirty.set(true);
    this.schedule();
  }

  /**
   * Saves pending edits. Resolves `true` when nothing is left unsaved, `false` when the save
   * failed (conflict, validation, network); the reason is in `conflict`, `fieldErrors` or
   * `saveError`. `overwrite` omits `expectedRevision` (last writer wins).
   */
  async save(options: { overwrite?: boolean } = {}): Promise<boolean> {
    this.clearTimer();
    while (this.inflight) {
      await this.inflight;
    }
    this.beforeSave?.();
    this.clearTimer();
    if (!this.isDirty()) {
      return !this.conflict();
    }
    if (this.conflict() && !options.overwrite) {
      return false;
    }
    this.inflight = this.send(options.overwrite === true);
    try {
      return await this.inflight;
    } finally {
      this.inflight = null;
    }
  }

  /** "Überschreiben": saves with the user's values although the record changed meanwhile. */
  async overwrite(): Promise<boolean> {
    this.conflict.set(null);
    this.beforeSave?.();
    const bodyWasDirty = this.bodyDirty();
    const revisionBefore = Number(this.record()?.revision);
    const saved = await this.save({ overwrite: true });
    // Without own body edits the editor still shows the old body although the other writer may
    // have changed it; the next body save would silently overwrite that text.
    if (saved && !bodyWasDirty) {
      await this.refreshBody(revisionBefore);
    }
    return saved;
  }

  /** Loads the current body into the editor (recreated), unless the user has own body edits. */
  private async refreshBody(revisionBefore: number): Promise<void> {
    const token = this.loadToken;
    let fresh: RecordDto;
    try {
      fresh = await firstValueFrom(
        this.api.workspace(this.slug).get('/records/{recordId}', {
          path: { recordId: this.recordId },
          query: { includeBody: true },
        }),
      );
    } catch {
      this.bodyStale.set(revisionBefore);
      this.saveError.set(
        'Der aktuelle Text konnte nicht geladen werden und kann veraltet sein. Bitte neu laden.',
      );
      return;
    }
    if (token !== this.loadToken) {
      return;
    }
    this.beforeSave?.(); // text typed while the GET ran must not vanish with the editor
    if (fresh.body === this.body()) {
      return;
    }
    if (this.bodyDirty()) {
      this.bodyStale.set(revisionBefore); // typed on the old text: let the next save conflict
      return;
    }
    this.record.update((current) => (current ? { ...current, body: fresh.body } : current));
    this.body.set(fresh.body ?? '');
    this.editorValue.set(fresh.body ?? '');
    this.loadCount.update((n) => n + 1);
  }

  /** Reloads server metadata (revision, `archivedAt`) without touching the edits. */
  private async refreshMeta(): Promise<void> {
    try {
      this.adopt(
        await firstValueFrom(
          this.api
            .workspace(this.slug)
            .get('/records/{recordId}', { path: { recordId: this.recordId } }),
        ),
      );
    } catch {
      // keep the current view
    }
  }

  /** Replaces the server metadata after archive or restore (edits stay untouched). */
  adopt(record: RecordDto): void {
    this.record.update((current) =>
      current ? { ...current, ...record, body: current.body } : record,
    );
  }

  private async send(overwrite: boolean): Promise<boolean> {
    const current = this.record();
    if (!current) {
      return false;
    }
    const sentTitle = this.title();
    const sentBody = this.body();
    const sentKeys = [...this.dirtyKeys()];
    const titleDirty = this.titleDirty();
    const bodyDirty = this.bodyDirty();
    const sentProps = Object.fromEntries(
      sentKeys.map((key) => [key, this.properties()[key] ?? null]),
    );

    const body: UpdateRecordBody = {
      ...(titleDirty ? { title: sentTitle } : {}),
      ...(sentKeys.length > 0 ? { properties: sentProps as never } : {}),
      ...(bodyDirty ? { body: sentBody } : {}),
      ...(overwrite
        ? {}
        : {
            expectedRevision:
              bodyDirty && this.bodyStale() !== null
                ? (this.bodyStale() as number)
                : Number(current.revision),
          }),
    };

    this.saving.set(true);
    this.saveError.set(null);
    this.fieldErrors.set({});
    try {
      const saved = await firstValueFrom(
        this.api.workspace(this.slug).patch('/records/{recordId}', {
          path: { recordId: this.recordId },
          body,
        }),
      );
      this.applySaved(saved, { sentTitle, sentBody, sentProps, titleDirty, bodyDirty });
      if (bodyDirty && this.body() === sentBody) {
        this.bodyStale.set(null);
      }
      this.savedAt.set(new Date());
      if (this.isDirty()) {
        this.schedule();
      }
      return true;
    } catch (error) {
      const failure = ApiError.from(error);
      this.applyFailure(failure);
      if (failure.code === 'record_archived') {
        await this.refreshMeta(); // the view turns read-only
      }
      return false;
    } finally {
      this.saving.set(false);
    }
  }

  /** Takes revision and metadata from the response; keeps everything the user changed since. */
  private applySaved(
    saved: RecordDto,
    sent: {
      sentTitle: string;
      sentBody: string;
      sentProps: PropertyValues;
      titleDirty: boolean;
      bodyDirty: boolean;
    },
  ): void {
    this.record.update((current) => ({ ...saved, body: current?.body ?? null }));
    if (sent.titleDirty && this.title() === sent.sentTitle) {
      this.titleDirty.set(false);
    }
    if (sent.bodyDirty && this.body() === sent.sentBody) {
      this.bodyDirty.set(false);
    }
    const stillDirty = new Set(this.dirtyKeys());
    for (const [key, value] of Object.entries(sent.sentProps)) {
      if (sameJson(this.properties()[key], value)) {
        stillDirty.delete(key);
      }
    }
    this.dirtyKeys.set(stillDirty);

    if (!this.titleDirty() && this.title().trim() !== saved.title) {
      this.title.set(saved.title);
    }
    const server = saved.properties as PropertyValues;
    this.properties.update((local) => {
      const next: PropertyValues = {};
      for (const key of Object.keys(server)) {
        next[key] =
          stillDirty.has(key) || sameTrimmed(local[key], server[key]) ? local[key] : server[key];
      }
      for (const key of stillDirty) {
        next[key] = local[key];
      }
      return next;
    });
  }

  private applyFailure(error: ApiError): void {
    if (error.status === 409 && error.code === 'revision_mismatch') {
      this.conflict.set({
        currentRevision: error.currentRevision ?? null,
        changedKeys: error.changedKeys ?? null,
      });
      return;
    }
    if (error.errors.length > 0) {
      const grouped: Record<string, string[]> = {};
      for (const e of error.errors) {
        (grouped[fieldKey(e.field)] ??= []).push(fieldErrorMessage(e));
      }
      this.fieldErrors.set(grouped);
      this.saveError.set('Bitte die markierten Felder korrigieren.');
      return;
    }
    this.saveError.set(describeError(error));
  }

  private clearFieldError(key: string): void {
    if (this.fieldErrors()[key]) {
      this.fieldErrors.update((errors) =>
        Object.fromEntries(Object.entries(errors).filter(([k]) => k !== key)),
      );
    }
  }

  private schedule(): void {
    this.clearTimer();
    this.timer = setTimeout(() => {
      this.timer = null;
      void this.save();
    }, AUTOSAVE_DELAY_MS);
  }

  private clearTimer(): void {
    if (this.timer !== null) {
      clearTimeout(this.timer);
      this.timer = null;
    }
  }
}
