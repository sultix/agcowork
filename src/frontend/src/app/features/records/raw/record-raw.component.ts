import { DatePipe } from '@angular/common';
import { HttpClient } from '@angular/common/http';
import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  computed,
  effect,
  inject,
  input,
  resource,
  signal,
  untracked,
} from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { ApiClient } from '../../../core/api/api-client';
import type { components } from '../../../core/api/generated/schema';
import { MembersStore } from '../../../core/workspace/members.store';
import { WorkspaceContext } from '../../../core/workspace/workspace-context.service';
import { RawViewComponent } from '../../../shared/raw-view/raw-view.component';
import { describeError } from '../../../shared/ui/api-error-text';
import { ConfirmDialogComponent } from '../../../shared/ui/confirm-dialog.component';
import { WorkspacesStore } from '../../workspaces/workspaces.store';
import { ConflictDialogComponent } from '../conflict-dialog.component';
import { diffProperties } from '../history/diff';
import { RecordHeaderComponent } from '../record-header.component';
import { RecordSaveService } from '../record-save.service';
import { editableProperties } from '../record-schema';

type Collection = components['schemas']['CollectionDto'];

type ParsedProperties =
  | { readonly ok: true; readonly value: Record<string, unknown> }
  | { readonly ok: false; readonly error: string };

const NON_PROPERTY_FIELDS = ['title', 'body'];

function format(properties: Record<string, unknown>): string {
  return JSON.stringify(properties, null, 2);
}

function parseProperties(text: string): ParsedProperties {
  try {
    const value: unknown = JSON.parse(text);
    if (value === null || typeof value !== 'object' || Array.isArray(value)) {
      return { ok: false, error: 'Die Properties müssen ein JSON-Objekt sein.' };
    }
    return { ok: true, value: value as Record<string, unknown> };
  } catch (error) {
    return { ok: false, error: error instanceof Error ? error.message : String(error) };
  }
}

/**
 * `/w/:slug/records/:recordId/raw`: the record as data. Properties as JSON and the body as
 * Markdown are editable (explicit save, no auto-save: half-typed JSON must not be saved); the
 * Markdown with frontmatter, as agents read it, is shown read-only. Removing a key from the
 * JSON deletes the property (sent as `null`).
 */
@Component({
  selector: 'ag-record-raw',
  imports: [
    DatePipe,
    ConfirmDialogComponent,
    ConflictDialogComponent,
    RawViewComponent,
    RecordHeaderComponent,
  ],
  providers: [RecordSaveService],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: {
    '(document:keydown)': 'onKeydown($event)',
    '(window:beforeunload)': 'onBeforeUnload($event)',
  },
  template: `
    <section class="mx-auto max-w-4xl p-6">
      @if (saver.loadError(); as message) {
        <p class="text-sm text-danger" role="alert" data-testid="load-error">{{ message }}</p>
      } @else if (saver.record(); as record) {
        <ag-record-header
          [recordId]="recordId()"
          [title]="record.title"
          [spaceId]="record.spaceId"
          [collectionId]="record.collectionId"
        />

        <div
          class="mt-2 flex flex-wrap items-center gap-3 text-sm text-muted"
          data-testid="record-meta"
        >
          <span data-testid="record-revision">Revision {{ record.revision }}</span>
          <span>
            aktualisiert {{ record.updatedAt | date: 'dd.MM.yyyy HH:mm' }} von
            {{ members.nameOf(record.updatedBy) }}
          </span>
          <span data-testid="save-status" role="status">{{ saveStatus() }}</span>
        </div>

        @if (readonly()) {
          <p class="mt-3 text-sm text-muted" data-testid="readonly-notice">
            {{
              record.archivedAt
                ? 'Archiviert und schreibgeschützt.'
                : 'Du hast hier nur Leserechte.'
            }}
          </p>
        } @else {
          <div class="mt-3">
            <button
              type="button"
              class="rounded-md bg-accent px-3 py-1.5 text-sm font-medium text-accent-fg disabled:opacity-60"
              data-testid="save"
              [disabled]="!canSave()"
              (click)="saveNow()"
            >
              Speichern
            </button>
          </div>
        }

        @if (saver.saveError(); as message) {
          <p
            class="mt-3 rounded-md border border-danger px-3 py-2 text-sm whitespace-pre-line text-danger"
            role="alert"
            data-testid="save-error"
          >
            {{ message }}
            @if (saver.bodyStale() !== null) {
              <button
                type="button"
                class="ml-2 rounded-md border border-danger px-2 py-0.5"
                data-testid="reload-stale"
                (click)="reload()"
              >
                Neu laden
              </button>
            }
          </p>
        }

        @if (ready()) {
          @for (message of generalErrors(); track $index) {
            <p class="mt-3 text-sm text-danger" role="alert" data-testid="general-error">
              {{ message }}
            </p>
          }
          <h2 class="mt-6 text-sm font-semibold">Properties (JSON)</h2>
          @if (errorList().length > 0) {
            <ul class="mt-2 text-sm text-danger" role="alert" data-testid="json-errors">
              @for (error of errorList(); track $index) {
                <li data-testid="json-error">
                  <span class="font-mono">{{ error.key }}</span
                  >: {{ error.message }}
                  @if (error.line !== null) {
                    <span class="text-muted"> (Zeile {{ error.line }})</span>
                  }
                </li>
              }
            </ul>
          }
          @if (parsed(); as p) {
            @if (!p.ok && !jsonSyntaxError()) {
              <p class="mt-2 text-sm text-danger" role="alert" data-testid="json-shape-error">
                {{ p.error }}
              </p>
            }
          }
          <div class="mt-2" data-testid="raw-properties">
            @for (version of [viewVersion()]; track version) {
              <ag-raw-view
                language="json"
                [value]="jsonText()"
                [readonly]="readonly()"
                (valueChange)="jsonText.set($event)"
              />
            }
          </div>

          <h2 class="mt-6 text-sm font-semibold">Body (Markdown)</h2>
          <div class="mt-2" data-testid="raw-body">
            @for (version of [saver.loadCount()]; track version) {
              <ag-raw-view
                language="markdown"
                [value]="saver.editorValue()"
                [readonly]="readonly()"
                (valueChange)="saver.setBody($event)"
              />
            }
          </div>

          <div class="mt-6 flex items-center justify-between">
            <h2 class="text-sm font-semibold">
              Markdown mit Frontmatter
              <span class="font-normal text-muted">(gespeicherter Stand, nur lesen)</span>
            </h2>
            <div class="flex items-center gap-2 text-sm">
              @if (copied()) {
                <span class="text-success" role="status" data-testid="copied">Kopiert</span>
              }
              <button
                type="button"
                class="rounded-md border border-border px-3 py-1 hover:bg-surface-hover disabled:opacity-60"
                data-testid="copy-markdown"
                [disabled]="frontmatter() === ''"
                (click)="copyMarkdown()"
              >
                Kopieren
              </button>
            </div>
          </div>
          @if (frontmatterError(); as message) {
            <p class="mt-2 text-sm text-danger" role="alert" data-testid="frontmatter-error">
              {{ message }}
            </p>
          }
          <div class="mt-2" data-testid="raw-frontmatter">
            <ag-raw-view language="markdown" [readonly]="true" [value]="frontmatter()" />
          </div>
        }
      } @else {
        <p class="text-sm text-muted">Wird geladen …</p>
      }
    </section>

    @if (saver.conflict(); as conflict) {
      <ag-conflict-dialog
        [conflict]="conflict"
        [saving]="saver.saving()"
        (reload)="reload()"
        (overwrite)="overwrite()"
      />
    }

    <ag-confirm-dialog
      [open]="leaveRequest() !== null"
      title="Änderungen verwerfen und verlassen?"
      message="Es gibt ungespeicherte Änderungen in den Rohdaten."
      confirmLabel="Verwerfen und verlassen"
      (confirmed)="answerLeave(true)"
      (cancelled)="answerLeave(false)"
    />
  `,
})
export class RecordRawComponent {
  protected readonly saver = inject(RecordSaveService);
  protected readonly members = inject(MembersStore);
  private readonly api = inject(ApiClient);
  private readonly http = inject(HttpClient);
  private readonly context = inject(WorkspaceContext);
  private readonly workspaces = inject(WorkspacesStore);

  /** Route parameter `:recordId`. */
  readonly recordId = input.required<string>();

  /** Text of the properties editor, including unsaved edits (may be empty while editing). */
  protected readonly jsonText = signal('');
  /** The editors have been filled from the loaded record; `false` while a record is (re)loading. */
  protected readonly jsonLoaded = signal(false);
  /** Changes with every (re)creation of the JSON editor. */
  protected readonly viewVersion = signal(0);
  protected readonly copied = signal(false);
  protected readonly leaveRequest = signal<{ resolve: (leave: boolean) => void } | null>(null);

  /** Set when the user asked to throw away the JSON edits (reload). */
  private forceReset = false;
  private filledAtLoad = -1;
  private copiedTimer: ReturnType<typeof setTimeout> | null = null;

  private readonly collectionId = computed(() => this.saver.record()?.collectionId ?? null);
  private readonly collection = resource<Collection, { slug: string; id: string } | undefined>({
    params: () => {
      const slug = this.context.slug();
      const id = this.collectionId();
      return slug && id ? { slug, id } : undefined;
    },
    loader: ({ params }) =>
      firstValueFrom(
        this.api
          .workspace(params.slug)
          .get('/collections/{collectionId}', { path: { collectionId: params.id } }),
      ),
  });

  protected readonly canWrite = computed(() => {
    const ws = this.workspaces.find(this.context.slug());
    return !!ws && !ws.archivedAt && ws.scopes.includes('write');
  });
  protected readonly readonly = computed(
    () => !this.canWrite() || !!this.saver.record()?.archivedAt,
  );

  /** The schema is known (or there is none): only then is it clear which keys are editable. */
  protected readonly ready = computed(
    () =>
      this.saver.record() !== null &&
      this.jsonLoaded() &&
      (this.collectionId() === null || this.collection.hasValue() || this.collection.error()),
  );

  /** Writable properties as the server has them; the JSON is diffed against this. */
  private readonly baseline = computed(() =>
    editableProperties(
      (this.saver.record()?.properties ?? {}) as Record<string, unknown>,
      this.collection.hasValue() ? this.collection.value().schema : null,
    ),
  );

  protected readonly parsed = computed(() => parseProperties(this.jsonText()));
  protected readonly jsonSyntaxError = computed(() => {
    try {
      JSON.parse(this.jsonText());
      return false;
    } catch {
      return true;
    }
  });
  private readonly changes = computed(() => {
    const parsed = this.parsed();
    return parsed.ok ? diffProperties(this.baseline(), parsed.value) : [];
  });

  private readonly unsaved = computed(() => this.changes().length > 0 || this.saver.isDirty());
  /** Invalid JSON adds nothing to save (`changes()` is empty then) but does not block the body. */
  protected readonly canSave = computed(
    () => !this.readonly() && this.jsonLoaded() && this.unsaved() && !this.saver.saving(),
  );
  /** Anything the user would lose by leaving: unsaved edits and unparseable JSON. */
  private readonly unsavedWork = computed(
    () => this.jsonLoaded() && (this.unsaved() || !this.parsed().ok),
  );
  protected readonly saveStatus = computed(() => {
    if (this.saver.saving()) {
      return 'Speichert …';
    }
    if (!this.parsed().ok) {
      return 'Ungültiges JSON wird nicht gespeichert';
    }
    if (this.unsaved()) {
      return 'Ungespeicherte Änderungen';
    }
    return this.saver.savedAt() ? 'Gespeichert' : '';
  });

  /** Validation messages (`properties.<key>`) with the line of the key in the JSON, if found. */
  protected readonly errorList = computed(() => {
    const lines = this.jsonText().split('\n');
    return Object.entries(this.saver.fieldErrors())
      .filter(([key]) => !NON_PROPERTY_FIELDS.includes(key))
      .flatMap(([key, messages]) => {
        const index = lines.findIndex((l) => l.startsWith(`  ${JSON.stringify(key)}:`));
        return messages.map((message) => ({
          key,
          message,
          line: index >= 0 ? index + 1 : null,
        }));
      });
  });

  /** Errors of `title` and `body`: not tied to a JSON key. */
  protected readonly generalErrors = computed(() =>
    Object.entries(this.saver.fieldErrors())
      .filter(([key]) => NON_PROPERTY_FIELDS.includes(key))
      .flatMap(([key, messages]) => messages.map((message) => `${key}: ${message}`)),
  );

  private readonly frontmatterResource = resource<
    string,
    { slug: string; id: string; revision: number } | undefined
  >({
    params: () => {
      const slug = this.context.slug();
      const record = this.saver.record();
      return slug && record
        ? { slug, id: record.id, revision: Number(record.revision) }
        : undefined;
    },
    loader: ({ params }) =>
      firstValueFrom(
        this.http.get(
          this.api.workspace(params.slug).url('/records/{recordId}', { recordId: params.id }),
          {
            params: { format: 'markdown' },
            headers: { Accept: 'text/markdown' },
            responseType: 'text',
            withCredentials: true,
          },
        ),
      ),
  });
  protected readonly frontmatter = computed(() =>
    this.frontmatterResource.hasValue() ? this.frontmatterResource.value() : '',
  );
  protected readonly frontmatterError = computed(() =>
    this.frontmatterResource.error() ? describeError(this.frontmatterResource.error()) : null,
  );

  constructor() {
    this.saver.autosave = false;
    inject(DestroyRef).onDestroy(() => {
      if (this.copiedTimer !== null) {
        clearTimeout(this.copiedTimer);
      }
    });

    effect(() => {
      const slug = this.context.slug();
      const id = this.recordId();
      if (slug && id) {
        untracked(() => {
          // Another record: nothing of the previous one may end up in this one.
          this.forceReset = true;
          this.jsonLoaded.set(false);
          this.jsonText.set('');
          void this.saver.load(slug, id);
        });
      }
    });

    // (Re)fill the JSON editor from the server state: after every load, unless the user has
    // JSON edits that must survive (the body-only refresh after "Überschreiben").
    effect(() => {
      const loadCount = this.saver.loadCount();
      const ready = this.saver.record() !== null && this.collectionSettled();
      if (!ready) {
        return;
      }
      untracked(() => {
        // A save changes the record but is no load: it must not touch the user's JSON text.
        if (this.jsonLoaded() && loadCount === this.filledAtLoad && !this.forceReset) {
          return;
        }
        this.filledAtLoad = loadCount;
        if (this.forceReset || !this.jsonLoaded() || this.changes().length === 0) {
          this.forceReset = false;
          this.jsonLoaded.set(true);
          this.jsonText.set(format(this.baseline()));
          this.viewVersion.update((n) => n + 1);
        }
      });
    });
  }

  private collectionSettled(): boolean {
    return this.collectionId() === null || this.collection.hasValue() || !!this.collection.error();
  }

  protected reload(): void {
    this.forceReset = true;
    void this.saver.reload();
  }

  protected async overwrite(): Promise<void> {
    const textAtSave = this.jsonText();
    this.applyChanges();
    const saved = await this.saver.overwrite().finally(() => this.saver.discardPropertyEdits());
    if (saved && this.jsonText() === textAtSave) {
      this.showSavedJson();
    }
  }

  protected async saveNow(): Promise<void> {
    if (!this.canSave()) {
      return;
    }
    const textAtSave = this.jsonText();
    this.applyChanges();
    // The JSON is the source of truth: a failed patch must not outlive the failed save.
    const saved = await this.saver.save().finally(() => this.saver.discardPropertyEdits());
    // The server may normalise values (trim, defaults): show what was stored.
    if (saved && this.jsonText() === textAtSave) {
      this.showSavedJson();
    }
  }

  /** Hands the JSON edits to the save service as a property patch (`null` removes a key). */
  private applyChanges(): void {
    for (const change of this.changes()) {
      this.saver.setProperty(change.key, change.kind === 'removed' ? null : change.new);
    }
  }

  private showSavedJson(): void {
    // Half-typed JSON (e.g. after a body-only save) must not be replaced silently.
    if (!this.parsed().ok) {
      return;
    }
    this.jsonText.set(format(this.baseline()));
  }

  protected onKeydown(event: KeyboardEvent): void {
    if ((event.metaKey || event.ctrlKey) && event.key.toLowerCase() === 's') {
      event.preventDefault();
      void this.saveNow();
    }
  }

  protected onBeforeUnload(event: BeforeUnloadEvent): void {
    if (!this.readonly() && this.unsavedWork()) {
      event.preventDefault();
    }
  }

  /** For the `canDeactivate` guard: unsaved raw edits are never saved implicitly, so ask. */
  canLeave(): Promise<boolean> {
    if (this.readonly() || !this.unsavedWork()) {
      return Promise.resolve(true);
    }
    return new Promise<boolean>((resolve) => this.leaveRequest.set({ resolve }));
  }

  protected answerLeave(leave: boolean): void {
    this.leaveRequest()?.resolve(leave);
    this.leaveRequest.set(null);
  }

  protected async copyMarkdown(): Promise<void> {
    try {
      await navigator.clipboard.writeText(this.frontmatter());
    } catch {
      return;
    }
    this.copied.set(true);
    if (this.copiedTimer !== null) {
      clearTimeout(this.copiedTimer);
    }
    this.copiedTimer = setTimeout(() => this.copied.set(false), 2000);
  }
}
