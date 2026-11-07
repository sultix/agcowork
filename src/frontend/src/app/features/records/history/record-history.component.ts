import { DatePipe } from '@angular/common';
import {
  ChangeDetectionStrategy,
  Component,
  computed,
  effect,
  inject,
  input,
  resource,
  signal,
  untracked,
} from '@angular/core';
import { NgIcon, provideIcons } from '@ng-icons/core';
import { lucideCircleAlert } from '@ng-icons/lucide';
import { HlmAlertImports } from '@spartan-ng/helm/alert';
import { HlmButton } from '@spartan-ng/helm/button';
import { HlmCheckbox } from '@spartan-ng/helm/checkbox';
import { HlmSkeleton } from '@spartan-ng/helm/skeleton';
import { HlmTableImports } from '@spartan-ng/helm/table';
import { firstValueFrom, fromEvent, takeUntil } from 'rxjs';
import { ApiClient } from '../../../core/api/api-client';
import { ApiError } from '../../../core/api/api-error';
import type { components } from '../../../core/api/generated/schema';
import { MembersStore } from '../../../core/workspace/members.store';
import { WorkspaceContext } from '../../../core/workspace/workspace-context.service';
import { describeError } from '../../../shared/ui/api-error-text';
import { ActorLabelComponent } from '../../../shared/ui/actor-label.component';
import { BadgeComponent } from '../../../shared/ui/badge.component';
import { ConfirmDialogComponent } from '../../../shared/ui/confirm-dialog.component';
import { WorkspacesStore } from '../../workspaces/workspaces.store';
import { RecordHeaderComponent } from '../record-header.component';
import { editableProperties, unknownProperties } from '../record-schema';
import { RevisionContent, diffRevisions, formatValue } from './diff';

type Revision = components['schemas']['RevisionDto'];
type RevisionSummary = components['schemas']['RevisionSummaryDto'];
type Collection = components['schemas']['CollectionDto'];
type RecordDto = components['schemas']['RecordDto'];

const PAGE_SIZE = 30;

const KIND_LABELS: Record<string, string> = {
  created: 'angelegt',
  updated: 'geändert',
  appended: 'ergänzt',
  archived: 'archiviert',
  restored: 'wiederhergestellt',
};

const EMPTY: RevisionContent = { title: '', properties: {}, body: '' };

interface Comparison {
  readonly from: Revision | null;
  readonly to: Revision;
}

/**
 * `/w/:slug/records/:recordId/history`: all revisions of a record (newest first), the diff
 * between two of them and "restore this revision", which writes the old state as a new revision.
 */
@Component({
  selector: 'ag-record-history',
  imports: [
    DatePipe,
    ActorLabelComponent,
    BadgeComponent,
    ConfirmDialogComponent,
    HlmAlertImports,
    HlmButton,
    HlmCheckbox,
    HlmSkeleton,
    HlmTableImports,
    NgIcon,
    RecordHeaderComponent,
  ],
  providers: [provideIcons({ lucideCircleAlert })],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <section class="mx-auto max-w-4xl p-6">
      @if (recordError(); as message) {
        <div hlmAlert variant="destructive" data-testid="load-error">
          <ng-icon name="lucideCircleAlert" />
          <p hlmAlertDescription>{{ message }}</p>
        </div>
      } @else if (record(); as record) {
        <ag-record-header
          [recordId]="recordId()"
          [title]="record.title"
          [spaceId]="record.spaceId"
          [collectionId]="record.collectionId"
        />

        <p class="mt-3 text-sm text-muted-foreground">
          Wähle eine Revision, um sie mit der vorherigen zu vergleichen, oder zwei Revisionen, um
          sie untereinander zu vergleichen.
        </p>

        @if (notice(); as message) {
          <p class="mt-3 text-sm text-success" role="status" data-testid="restore-notice">
            {{ message }}
          </p>
        }
        @if (actionError(); as message) {
          <div hlmAlert variant="destructive" class="mt-3" data-testid="action-error">
            <ng-icon name="lucideCircleAlert" />
            <p hlmAlertDescription class="whitespace-pre-line">{{ message }}</p>
          </div>
        }
        @if (listError(); as message) {
          <div hlmAlert variant="destructive" class="mt-3" data-testid="history-error">
            <ng-icon name="lucideCircleAlert" />
            <p hlmAlertDescription>{{ message }}</p>
          </div>
        }

        <ul
          class="mt-4 divide-y divide-border rounded-xl border border-border bg-card text-card-foreground"
          data-testid="revisions"
        >
          @for (rev of items(); track rev.no) {
            <li
              class="flex flex-wrap items-center gap-3 px-4 py-2 text-sm"
              [class.bg-muted]="isSelected(rev.no)"
              data-testid="revision"
              [attr.data-no]="rev.no"
            >
              <hlm-checkbox
                [checked]="isSelected(rev.no)"
                [aria-label]="'Revision ' + rev.no + ' vergleichen'"
                data-testid="revision-select"
                (checkedChange)="toggle(toNumber(rev.no))"
              />
              <span class="w-12 font-mono" data-testid="revision-no">#{{ rev.no }}</span>
              <ag-badge>{{ kindLabel(rev.kind) }}</ag-badge>
              <ag-actor-label [actorId]="rev.actorId" />
              <span class="text-muted-foreground">{{ rev.at | date: 'dd.MM.yyyy HH:mm' }}</span>
              @if (rev.reason) {
                <span class="min-w-0 truncate text-muted-foreground" data-testid="revision-reason"
                  >„{{ rev.reason }}“</span
                >
              }
              @if (canRestore() && toNumber(rev.no) !== currentRevision()) {
                <button
                  type="button"
                  hlmBtn
                  variant="outline"
                  size="xs"
                  class="ml-auto"
                  data-testid="revision-restore"
                  (click)="prepareRestore(toNumber(rev.no))"
                >
                  Diese Revision wiederherstellen
                </button>
              }
            </li>
          } @empty {
            @if (!listLoading() && !listError()) {
              <li class="px-4 py-3 text-muted-foreground">Keine Revisionen.</li>
            }
          }
        </ul>
        @if (nextCursor()) {
          <button
            type="button"
            hlmBtn
            variant="outline"
            class="mt-3"
            data-testid="load-more"
            [disabled]="listLoading()"
            (click)="loadMore()"
          >
            Mehr laden
          </button>
        }

        @if (pair(); as pair) {
          <h2 class="mt-8 text-lg font-semibold" data-testid="diff-heading">
            @if (pair.from !== null) {
              Änderungen von #{{ pair.from }} zu #{{ pair.to }}
            } @else {
              Inhalt von #{{ pair.to }}
            }
          </h2>
          @if (comparisonError(); as message) {
            <div hlmAlert variant="destructive" class="mt-2" data-testid="diff-error">
              <ng-icon name="lucideCircleAlert" />
              <p hlmAlertDescription>{{ message }}</p>
            </div>
          } @else if (diff(); as diff) {
            <div data-testid="diff">
              @if (diff.title; as title) {
                <h3 class="mt-4 text-sm font-semibold">Titel</h3>
                <p class="mt-1 text-sm" data-testid="diff-title">
                  <del class="text-destructive">{{ title.old }}</del>
                  →
                  <ins class="text-success no-underline">{{ title.new }}</ins>
                </p>
              }

              <h3 class="mt-4 text-sm font-semibold">Properties</h3>
              @if (diff.properties.length > 0) {
                <table hlmTable class="mt-1 text-left" data-testid="diff-properties">
                  <thead hlmTHead>
                    <tr hlmTr>
                      <th hlmTh class="h-8 w-1/4 text-muted-foreground">Key</th>
                      <th hlmTh class="h-8 w-3/8 text-muted-foreground">Alt</th>
                      <th hlmTh class="h-8 w-3/8 text-muted-foreground">Neu</th>
                    </tr>
                  </thead>
                  <tbody hlmTBody>
                    @for (change of diff.properties; track change.key) {
                      <tr
                        hlmTr
                        data-testid="diff-property"
                        [attr.data-key]="change.key"
                        [attr.data-kind]="change.kind"
                      >
                        <td hlmTd class="py-1 font-mono">{{ change.key }}</td>
                        <td
                          hlmTd
                          class="py-1 break-all whitespace-normal text-destructive"
                          data-testid="diff-old"
                        >
                          {{ format(change.old) }}
                        </td>
                        <td
                          hlmTd
                          class="py-1 break-all whitespace-normal text-success"
                          data-testid="diff-new"
                        >
                          {{ format(change.new) }}
                        </td>
                      </tr>
                    }
                  </tbody>
                </table>
              } @else {
                <p class="mt-1 text-sm text-muted-foreground">
                  Keine Änderungen an den Properties.
                </p>
              }

              <h3 class="mt-4 text-sm font-semibold">Body</h3>
              @if (diff.bodyChanged) {
                <pre
                  class="mt-1 overflow-x-auto rounded-md border border-border bg-card text-sm"
                  data-testid="diff-body"
                ><code>@for (line of diff.body; track $index) {<span class="block px-2 whitespace-pre-wrap" data-testid="diff-line" [attr.data-kind]="line.kind" [class]="lineClass(line.kind)">{{ linePrefix(line.kind) }}{{ line.text }}</span>}</code></pre>
              } @else {
                <p class="mt-1 text-sm text-muted-foreground">Der Body ist unverändert.</p>
              }
            </div>
          }
        }
      } @else {
        <div class="flex flex-col gap-3" data-testid="record-loading">
          <hlm-skeleton class="h-6 w-48" />
          <hlm-skeleton class="h-40 w-full" />
        </div>
      }
    </section>

    <ag-confirm-dialog
      [open]="restorePreview() !== null"
      [title]="'Revision ' + restorePreview()?.no + ' wiederherstellen?'"
      [message]="restoreMessage()"
      confirmLabel="Wiederherstellen"
      [busy]="restoring()"
      (confirmed)="restore()"
      (cancelled)="restorePreview.set(null)"
    />
  `,
})
export class RecordHistoryComponent {
  protected readonly members = inject(MembersStore);
  private readonly api = inject(ApiClient);
  private readonly context = inject(WorkspaceContext);
  private readonly workspaces = inject(WorkspacesStore);

  /** Route parameter `:recordId`. */
  readonly recordId = input.required<string>();

  // ---- record ----------------------------------------------------------------------------

  private readonly recordResource = resource<RecordDto, { slug: string; id: string } | undefined>({
    params: () => {
      const slug = this.context.slug();
      const id = this.recordId();
      return slug && id ? { slug, id } : undefined;
    },
    loader: ({ params }) =>
      firstValueFrom(
        this.api
          .workspace(params.slug)
          .get('/records/{recordId}', { path: { recordId: params.id } }),
      ),
  });
  protected readonly record = computed(() =>
    this.recordResource.hasValue() ? this.recordResource.value() : null,
  );
  protected readonly recordError = computed(() =>
    this.recordResource.error() ? describeError(this.recordResource.error()) : null,
  );
  protected readonly currentRevision = computed(() => Number(this.record()?.revision));

  /** Stable id of the record's collection: the resource below must not reload with every reload of the record. */
  private readonly collectionId = computed(() => this.record()?.collectionId ?? null);
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

  protected readonly canRestore = computed(() => {
    const ws = this.workspaces.find(this.context.slug());
    // Without the schema computed keys cannot be told apart from writable ones.
    const schemaKnown = this.collectionId() === null || this.collection.hasValue();
    return (
      !!ws &&
      !ws.archivedAt &&
      ws.scopes.includes('write') &&
      !this.record()?.archivedAt &&
      schemaKnown
    );
  });

  // ---- revision list ---------------------------------------------------------------------

  protected readonly items = signal<readonly RevisionSummary[]>([]);
  protected readonly nextCursor = signal<string | null>(null);
  protected readonly listLoading = signal(false);
  protected readonly listError = signal<string | null>(null);
  private listToken = 0;

  // ---- comparison ------------------------------------------------------------------------

  /** Selected revision numbers, in the order they were chosen (at most two). */
  protected readonly selected = signal<readonly number[]>([]);

  /** Revisions to compare: `from` (older, `null` = empty) and `to`. */
  protected readonly pair = computed<{ from: number | null; to: number } | null>(() => {
    const sel = this.selected();
    if (sel.length === 0) {
      return null;
    }
    if (sel.length === 1) {
      return { from: sel[0] > 1 ? sel[0] - 1 : null, to: sel[0] };
    }
    const [a, b] = [...sel].sort((x, y) => x - y);
    return { from: a, to: b };
  });

  private readonly comparison = resource<
    Comparison,
    { slug: string; id: string; from: number | null; to: number } | undefined
  >({
    params: () => {
      const slug = this.context.slug();
      const pair = this.pair();
      return slug && pair ? { slug, id: this.recordId(), ...pair } : undefined;
    },
    loader: async ({ params, abortSignal }) => {
      const client = this.api.workspace(params.slug);
      const fetch = (no: number) =>
        // a newer selection supersedes this one: drop the request
        firstValueFrom(
          client
            .get('/records/{recordId}/revisions/{no}', {
              path: { recordId: params.id, no },
            })
            .pipe(takeUntil(fromEvent(abortSignal, 'abort'))),
        );
      const [from, to] = await Promise.all([
        params.from === null ? Promise.resolve(null) : fetch(params.from),
        fetch(params.to),
      ]);
      return { from, to };
    },
  });
  protected readonly comparisonError = computed(() =>
    this.comparison.error() ? describeError(this.comparison.error()) : null,
  );
  protected readonly diff = computed(() => {
    if (!this.comparison.hasValue()) {
      return null;
    }
    const { from, to } = this.comparison.value();
    return diffRevisions(from ?? EMPTY, to);
  });

  // ---- restore ---------------------------------------------------------------------------

  /** The revision to restore, loaded for the confirmation; `dropped` = keys the schema no longer knows. */
  protected readonly restorePreview = signal<{
    no: number;
    revision: Revision;
    dropped: string[];
  } | null>(null);
  protected readonly restoreMessage = computed(() => {
    const preview = this.restorePreview();
    const base =
      'Titel, Properties und Body dieser Revision werden als neue Revision gespeichert. Nichts wird gelöscht; der jetzige Stand bleibt in der Historie.';
    return preview && preview.dropped.length > 0
      ? `${base} Nicht wiederhergestellt werden Properties, die das Schema nicht mehr kennt: ${preview.dropped.join(', ')}.`
      : base;
  });
  protected readonly restoring = signal(false);
  protected readonly actionError = signal<string | null>(null);
  protected readonly notice = signal<string | null>(null);

  constructor() {
    effect(() => {
      const slug = this.context.slug();
      const id = this.recordId();
      if (slug && id) {
        untracked(() => {
          this.selected.set([]);
          void this.loadList(slug, id, false);
        });
      }
    });
  }

  protected toNumber(value: number | string): number {
    return Number(value);
  }

  protected kindLabel(kind: string): string {
    return KIND_LABELS[kind] ?? kind;
  }

  protected format(value: unknown): string {
    return formatValue(value);
  }

  protected lineClass(kind: string): string {
    return kind === 'add'
      ? 'bg-success/10 text-success'
      : kind === 'remove'
        ? 'bg-destructive/10 text-destructive'
        : 'text-muted-foreground';
  }

  protected linePrefix(kind: string): string {
    return kind === 'add' ? '+ ' : kind === 'remove' ? '- ' : '  ';
  }

  protected isSelected(no: number | string): boolean {
    return this.selected().includes(Number(no));
  }

  protected toggle(no: number): void {
    this.selected.update((sel) =>
      sel.includes(no) ? sel.filter((n) => n !== no) : [...sel, no].slice(-2),
    );
  }

  protected loadMore(): Promise<void> {
    return this.loadList(this.context.slug() ?? '', this.recordId(), true);
  }

  private async loadList(slug: string, id: string, append: boolean): Promise<void> {
    if (slug === '') {
      return;
    }
    const token = ++this.listToken;
    const cursor = append ? this.nextCursor() : null;
    if (!append) {
      this.items.set([]);
      this.nextCursor.set(null);
    }
    this.listLoading.set(true);
    this.listError.set(null);
    try {
      const page = await firstValueFrom(
        this.api.workspace(slug).get('/records/{recordId}/history', {
          path: { recordId: id },
          query: { limit: PAGE_SIZE, ...(cursor ? { cursor } : {}) },
        }),
      );
      if (token !== this.listToken) {
        return;
      }
      this.items.update((current) => (append ? [...current, ...page.items] : page.items));
      this.nextCursor.set(page.nextCursor);
    } catch (error) {
      if (token === this.listToken) {
        this.listError.set(describeError(error));
      }
    } finally {
      if (token === this.listToken) {
        this.listLoading.set(false);
      }
    }
  }

  /** Writes the chosen revision's title, properties and body as a new revision. */
  protected async prepareRestore(no: number): Promise<void> {
    const slug = this.context.slug();
    const record = this.record();
    if (!slug || !record) {
      return;
    }
    this.actionError.set(null);
    this.notice.set(null);
    try {
      const revision = await firstValueFrom(
        this.api.workspace(slug).get('/records/{recordId}/revisions/{no}', {
          path: { recordId: record.id, no },
        }),
      );
      const schema = this.collection.hasValue() ? this.collection.value().schema : null;
      const writable = editableProperties(revision.properties, schema);
      this.restorePreview.set({ no, revision, dropped: unknownProperties(writable, schema) });
    } catch (error) {
      this.actionError.set(describeError(ApiError.from(error)));
    }
  }

  /** Writes the previewed revision's title, properties and body as a new revision. */
  protected async restore(): Promise<void> {
    const preview = this.restorePreview();
    const slug = this.context.slug();
    const record = this.record();
    if (!preview || !slug || !record) {
      return;
    }
    const { no, revision } = preview;
    this.restoring.set(true);
    this.actionError.set(null);
    this.notice.set(null);
    try {
      const schema = this.collection.hasValue() ? this.collection.value().schema : null;
      const known = (props: Record<string, unknown>) => {
        const writable = editableProperties(props, schema);
        const unknown = new Set(unknownProperties(writable, schema));
        return Object.fromEntries(Object.entries(writable).filter(([key]) => !unknown.has(key)));
      };
      const target = known(revision.properties);
      // Keys the record has now but the revision lacks must go away: send them as null.
      const properties: Record<string, unknown> = { ...target };
      for (const key of Object.keys(known(record.properties))) {
        if (!(key in target) || target[key] === undefined) {
          properties[key] = null;
        }
      }
      const saved = await firstValueFrom(
        this.api.workspace(slug).patch('/records/{recordId}', {
          path: { recordId: record.id },
          body: {
            title: revision.title,
            properties: properties as never,
            body: revision.body,
            expectedRevision: this.currentRevision(),
            reason: `Revision ${no} wiederhergestellt`,
          },
        }),
      );
      this.notice.set(
        Number(saved.revision) === this.currentRevision()
          ? `Revision ${no} entspricht dem aktuellen Stand: keine Änderung, keine neue Revision.`
          : `Revision ${no} wiederhergestellt (neue Revision ${Number(saved.revision)}).`,
      );
      this.selected.set([]);
      this.recordResource.reload();
      await this.loadList(slug, record.id, false);
    } catch (error) {
      this.actionError.set(describeError(ApiError.from(error)));
      this.recordResource.reload(); // a conflict means the record moved on
    } finally {
      this.restoring.set(false);
      this.restorePreview.set(null);
    }
  }
}
