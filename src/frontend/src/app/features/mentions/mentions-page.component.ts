import { DatePipe } from '@angular/common';
import {
  ChangeDetectionStrategy,
  Component,
  computed,
  effect,
  inject,
  input,
  signal,
  untracked,
} from '@angular/core';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { NgIcon, provideIcons } from '@ng-icons/core';
import { lucideCheckCheck, lucideCircleAlert } from '@ng-icons/lucide';
import { HlmAlertImports } from '@spartan-ng/helm/alert';
import { HlmButton } from '@spartan-ng/helm/button';
import { HlmCheckbox } from '@spartan-ng/helm/checkbox';
import { HlmLabel } from '@spartan-ng/helm/label';
import { HlmSkeleton } from '@spartan-ng/helm/skeleton';
import { firstValueFrom } from 'rxjs';
import { ApiClient } from '../../core/api/api-client';
import { SessionStore } from '../../core/auth/session.store';
import { liveReload } from '../../core/realtime/live-reload';
import { WorkspaceContext } from '../../core/workspace/workspace-context.service';
import { describeError } from '../../shared/ui/api-error-text';
import { Mention, MentionsStore, mayChangeMentions } from './mentions.store';

const PAGE_SIZE = 20;

interface Row {
  readonly mention: Mention;
  readonly link: string[];
  readonly query: { comment: string };
}

/**
 * `/w/:slug/mentions?unreadOnly=true`: the caller's inbox of mentions, newest first. The URL is
 * the state (filter); a click opens the record with the comment (`?comment=<id>`, read by the
 * comment sidebar) and marks the mention read. The excerpt is shown as text, never as markup.
 */
@Component({
  selector: 'ag-mentions-page',
  imports: [
    DatePipe,
    NgIcon,
    RouterLink,
    HlmAlertImports,
    HlmButton,
    HlmCheckbox,
    HlmLabel,
    HlmSkeleton,
  ],
  providers: [provideIcons({ lucideCheckCheck, lucideCircleAlert })],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <section class="mx-auto flex max-w-4xl flex-col gap-6 p-6">
      <div class="flex flex-col gap-1">
        <h1 class="text-2xl font-semibold tracking-tight">Erwähnungen</h1>
        <p class="text-sm text-muted-foreground">
          Kommentare, in denen du erwähnt wurdest, neueste zuerst.
        </p>
      </div>

      <div class="flex flex-wrap items-center justify-between gap-4">
        <div class="flex items-center gap-2">
          <hlm-checkbox
            inputId="filter-unread"
            data-testid="filter-unread"
            [checked]="unreadOnlyFilter()"
            (checkedChange)="setUnreadOnly($event === true)"
          />
          <label hlmLabel class="font-normal" for="filter-unread">Nur ungelesene</label>
        </div>
        <button
          hlmBtn
          type="button"
          variant="outline"
          size="sm"
          data-testid="mark-all-read"
          [disabled]="(store.unreadCount() === 0 && !hasUnreadListed()) || marking()"
          (click)="markAll()"
        >
          <ng-icon name="lucideCheckCheck" />
          Alle als gelesen markieren
        </button>
      </div>

      @if (error(); as message) {
        <div hlmAlert variant="destructive" data-testid="mentions-error">
          <ng-icon name="lucideCircleAlert" />
          <p hlmAlertDescription>{{ message }}</p>
        </div>
      }

      @if (rows().length > 0) {
        <ul class="divide-y rounded-xl border" data-testid="mentions-list">
          @for (row of rows(); track row.mention.id) {
            <li
              class="flex flex-col gap-1 px-4 py-3"
              data-testid="mention"
              [attr.data-unread]="row.mention.readAt === null"
              [class.bg-muted]="row.mention.readAt === null"
            >
              <div class="flex flex-wrap items-baseline justify-between gap-x-4 gap-y-1">
                <span class="flex flex-wrap items-center gap-2">
                  @if (row.mention.readAt === null) {
                    <span class="size-2 rounded-full bg-primary" aria-hidden="true"></span>
                    <span class="sr-only" data-testid="mention-unread">Ungelesen:</span>
                  }
                  <a
                    [routerLink]="row.link"
                    [queryParams]="row.query"
                    class="underline underline-offset-4"
                    [class.font-semibold]="row.mention.readAt === null"
                    [class.font-medium]="row.mention.readAt !== null"
                    data-testid="mention-link"
                    (click)="open(row.mention)"
                    >{{ row.mention.recordTitle || 'Ohne Titel' }}</a
                  >
                </span>
                <span class="text-xs text-muted-foreground" data-testid="mention-time">{{
                  row.mention.createdAt | date: 'dd.MM.yyyy HH:mm'
                }}</span>
              </div>
              <p class="text-xs text-muted-foreground" data-testid="mention-by">
                {{ row.mention.byActorName }} hat dich erwähnt
              </p>
              <p class="text-sm break-words" data-testid="mention-excerpt">
                {{ row.mention.excerpt }}
              </p>
            </li>
          }
        </ul>
      } @else if (loading()) {
        <div class="flex flex-col gap-2" aria-busy="true" aria-label="Wird geladen">
          <hlm-skeleton class="h-16 rounded-lg" />
          <hlm-skeleton class="h-16 rounded-lg" />
          <hlm-skeleton class="h-16 rounded-lg" />
        </div>
      } @else if (loaded() && !error()) {
        <div
          class="flex flex-col items-center gap-1 rounded-xl border border-dashed p-10 text-center"
        >
          <p class="font-medium" role="status" data-testid="mentions-empty">
            {{ unreadOnlyFilter() ? 'Keine ungelesenen Erwähnungen.' : 'Keine Erwähnungen.' }}
          </p>
          <p class="text-sm text-muted-foreground">
            Erwähnungen erscheinen hier, sobald dich jemand in einem Kommentar nennt.
          </p>
        </div>
      }
      @if (nextCursor()) {
        <div>
          <button
            hlmBtn
            type="button"
            variant="outline"
            data-testid="load-more"
            [disabled]="loading()"
            (click)="loadMore()"
          >
            Mehr laden
          </button>
        </div>
      }
    </section>
  `,
})
export class MentionsPageComponent {
  protected readonly store = inject(MentionsStore);
  private readonly context = inject(WorkspaceContext);
  private readonly session = inject(SessionStore);
  private readonly api = inject(ApiClient);
  private readonly router = inject(Router);
  private readonly route = inject(ActivatedRoute);

  // Query parameter, bound by the router (withComponentInputBinding).
  readonly unreadOnly = input<string | undefined>();

  protected readonly unreadOnlyFilter = computed(() => this.unreadOnly() === 'true');

  private readonly items = signal<readonly Mention[]>([]);
  protected readonly nextCursor = signal<string | null>(null);
  protected readonly loading = signal(false);
  protected readonly loaded = signal(false);
  protected readonly marking = signal(false);
  protected readonly error = signal<string | null>(null);
  private token = 0;

  protected readonly hasUnreadListed = computed(() => this.items().some((m) => m.readAt === null));
  /** Mentions whose "read" call is under way (a second click must not send another). */
  private readonly pending = new Set<string>();

  protected readonly rows = computed<Row[]>(() => {
    const slug = this.context.slug() ?? '';
    return this.items().map((mention) => ({
      mention,
      link: ['/w', slug, 'records', mention.recordId],
      query: { comment: mention.commentId },
    }));
  });

  /** What is listed; a new value starts over with the first page. */
  private readonly request = computed(() => ({
    slug: this.context.slug(),
    unreadOnly: this.unreadOnlyFilter(),
  }));

  constructor() {
    effect(() => {
      const { slug, unreadOnly } = this.request();
      untracked(() => {
        if (slug) {
          void this.load(slug, unreadOnly, null);
        }
      });
    });
    // A new mention of the caller (or missed events): show it on top by reading from the start.
    liveReload({
      slug: () => this.context.slug(),
      matches: (event) => mayChangeMentions(event, this.session.me()?.id),
      reload: () => {
        const { slug, unreadOnly } = this.request();
        if (slug) {
          void this.load(slug, unreadOnly, null, true);
        }
      },
    });
  }

  protected setUnreadOnly(value: boolean): void {
    void this.router.navigate([], {
      relativeTo: this.route,
      queryParams: { unreadOnly: value ? 'true' : null },
      queryParamsHandling: 'merge',
    });
  }

  protected loadMore(): void {
    const { slug, unreadOnly } = this.request();
    const cursor = this.nextCursor();
    if (slug && cursor) {
      void this.load(slug, unreadOnly, cursor);
    }
  }

  /** Click on a mention: the link navigates, the mention is marked read in the background. */
  protected open(mention: Mention): void {
    if (mention.readAt !== null || this.pending.has(mention.id)) {
      return;
    }
    this.pending.add(mention.id);
    this.setRead([mention.id]);
    this.store
      .markRead([mention.id])
      .catch(() => {
        // Not marked: the mention stays unread and is shown as such after the next load.
      })
      .finally(() => this.pending.delete(mention.id));
  }

  protected async markAll(): Promise<void> {
    const slug = this.context.slug();
    this.marking.set(true);
    this.error.set(null);
    try {
      const result = await this.store.markAllRead();
      // Skip when nothing was sent (no workspace) or the workspace changed meanwhile.
      if (result && this.context.slug() === slug) {
        this.setRead(null);
      }
    } catch (error) {
      this.error.set(describeError(error));
    } finally {
      this.marking.set(false);
    }
  }

  /** Marks listed mentions as read locally (`null`: all of them). */
  private setRead(ids: readonly string[] | null): void {
    const at = new Date().toISOString();
    this.items.update((items) =>
      items.map((m) =>
        m.readAt === null && (ids === null || ids.includes(m.id)) ? { ...m, readAt: at } : m,
      ),
    );
  }

  /**
   * Reads a page. `refresh` re-reads the first page while the list stays on screen (live
   * update); the older pages loaded so far are kept below the fresh one.
   */
  private async load(
    slug: string,
    unreadOnly: boolean,
    cursor: string | null,
    refresh = false,
  ): Promise<void> {
    const token = ++this.token;
    if (cursor === null && !refresh) {
      this.items.set([]);
      this.nextCursor.set(null);
      this.loaded.set(false);
    }
    this.loading.set(true);
    this.error.set(null);
    try {
      const page = await firstValueFrom(
        this.api.workspace(slug).get('/mentions', {
          query: {
            ...(unreadOnly ? { unreadOnly: true } : {}),
            limit: PAGE_SIZE,
            ...(cursor ? { cursor } : {}),
          },
        }),
      );
      if (token !== this.token) {
        return;
      }
      this.store.accept(Number(page.unreadCount));
      if (refresh) {
        const fresh = new Set(page.items.map((m) => m.id));
        const edge =
          page.items.length > 0 ? Date.parse(page.items[page.items.length - 1].createdAt) : 0;
        const older = page.nextCursor
          ? this.items().filter((m) => !fresh.has(m.id) && Date.parse(m.createdAt) < edge)
          : [];
        this.nextCursor.set(older.length > 0 ? this.nextCursor() : (page.nextCursor ?? null));
        this.items.set([...page.items, ...older]);
      } else {
        this.items.update((current) => {
          const listed = new Set(current.map((m) => m.id));
          return [...current, ...page.items.filter((m) => !listed.has(m.id))];
        });
        this.nextCursor.set(page.nextCursor ?? null);
      }
      this.loaded.set(true);
    } catch (error) {
      if (token === this.token) {
        this.loaded.set(true);
        this.error.set(describeError(error));
      }
    } finally {
      if (token === this.token) {
        this.loading.set(false);
      }
    }
  }
}
