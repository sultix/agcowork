import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  InjectionToken,
  computed,
  inject,
  input,
  output,
} from '@angular/core';
import { Router, RouterLink } from '@angular/router';
import { NgIcon, provideIcons } from '@ng-icons/core';
import { lucideX } from '@ng-icons/lucide';
import { HlmBadge } from '@spartan-ng/helm/badge';
import { HlmButton } from '@spartan-ng/helm/button';
import { HlmSkeleton } from '@spartan-ng/helm/skeleton';
import { RecordTitleStore } from '../../core/records/record-title.store';
import { WorkspaceContext } from '../../core/workspace/workspace-context.service';

/**
 * How long a click on a chip in a table cell waits for a second click: a double click opens the
 * cell's editor, a single click follows the link.
 */
export const CHIP_CLICK_DELAY_MS = new InjectionToken<number>('CHIP_CLICK_DELAY_MS', {
  providedIn: 'root',
  factory: () => 250,
});

/**
 * How chip titles behave: `link` a normal link; `cell` a link that waits for a possible double
 * click (which belongs to the table cell: inline edit) and stays out of the tab order; `none`
 * plain text (inside editors, where navigating would leave the edit).
 */
export type ChipNavigation = 'link' | 'cell' | 'none';

/**
 * Relation values as chips: the title of each referenced record as a link to it (titles come
 * from the shared {@link RecordTitleStore}, so many chips cost one lookup and follow renames
 * live). Archived targets are struck through (with "archiviert" for screen readers), unknown or
 * foreign ids read "Nicht verfügbar" without a link. Titles are rendered as text only. With
 * `max`, the chips beyond it are summed up as "+N". See {@link ChipNavigation} for the links.
 */
@Component({
  selector: 'ag-relation-chips',
  imports: [NgIcon, HlmBadge, HlmButton, HlmSkeleton, RouterLink],
  providers: [provideIcons({ lucideX })],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'flex min-w-0 items-center gap-1' },
  template: `
    @for (chip of shown(); track $index) {
      <span
        hlmBadge
        variant="outline"
        class="max-w-56 min-w-0 justify-start font-normal"
        data-testid="relation-chip"
        [attr.data-id]="chip.id"
        [attr.data-state]="chip.entry.state"
      >
        @if (chip.linkable) {
          @switch (navigation()) {
            @case ('link') {
              <a
                class="min-w-0 truncate underline underline-offset-4"
                data-testid="relation-chip-link"
                [class.line-through]="chip.archived"
                [class.text-muted-foreground]="chip.archived"
                [class.font-mono]="chip.failed"
                [attr.title]="chip.failed ? failedHint : null"
                [routerLink]="['/w', slug(), 'records', chip.id]"
                >{{ chip.label }}</a
              >
            }
            @case ('cell') {
              <a
                class="min-w-0 truncate underline underline-offset-4"
                data-testid="relation-chip-link"
                tabindex="-1"
                [class.line-through]="chip.archived"
                [class.text-muted-foreground]="chip.archived"
                [class.font-mono]="chip.failed"
                [attr.title]="chip.failed ? failedHint : null"
                [attr.href]="href(chip.id)"
                (click)="onCellClick($event, chip.id)"
                >{{ chip.label }}</a
              >
            }
            @default {
              <span
                class="min-w-0 truncate"
                data-testid="relation-chip-text"
                [class.line-through]="chip.archived"
                [class.text-muted-foreground]="chip.archived"
                [class.font-mono]="chip.failed"
                [attr.title]="chip.failed ? failedHint : null"
                >{{ chip.label }}</span
              >
            }
          }
          @if (chip.archived) {
            <span class="sr-only"> (archiviert)</span>
          }
        } @else if (chip.entry.state === 'missing') {
          <span class="truncate text-muted-foreground italic" data-testid="relation-chip-missing"
            >Nicht verfügbar</span
          >
        } @else {
          <span
            hlmSkeleton
            class="inline-block h-3 w-12"
            data-testid="relation-chip-loading"
          ></span>
          <span class="sr-only">Lädt</span>
        }
        @if (removable()) {
          <button
            type="button"
            hlmBtn
            variant="ghost"
            size="icon-xs"
            class="size-4 shrink-0 rounded-full hover:text-destructive"
            data-testid="relation-remove"
            [attr.aria-label]="'Entfernen: ' + chip.label"
            (click)="remove.emit(chip.id)"
          >
            <ng-icon name="lucideX" />
          </button>
        }
      </span>
    }
    @if (hidden() > 0) {
      <span class="text-xs text-muted-foreground" data-testid="relation-more"
        >+{{ hidden() }}<span class="sr-only"> weitere</span></span
      >
    }
  `,
})
export class RelationChipsComponent {
  private readonly titles = inject(RecordTitleStore);
  private readonly context = inject(WorkspaceContext);
  private readonly router = inject(Router);
  private readonly clickDelay = inject(CHIP_CLICK_DELAY_MS);
  private clickTimer: ReturnType<typeof setTimeout> | null = null;

  protected readonly failedHint = 'Titel konnte nicht geladen werden';

  /** Ids of the referenced records, in their stored order. */
  readonly ids = input.required<readonly string[]>();
  /** Show at most this many chips, the rest as "+N" (`null`: all). */
  readonly max = input<number | null>(null);
  /** Each chip gets a remove button that emits {@link remove}. */
  readonly removable = input(false);
  readonly navigation = input<ChipNavigation>('link');
  readonly remove = output<string>();

  protected readonly slug = computed(() => this.context.slug() ?? '');

  protected readonly shown = computed(() => {
    const max = this.max();
    const ids = max === null ? this.ids() : this.ids().slice(0, max);
    return ids.map((id) => {
      const entry = this.titles.get(id);
      return {
        id,
        entry,
        label: this.titles.label(id),
        archived: entry.state === 'found' && entry.archived,
        failed: entry.state === 'failed',
        linkable: entry.state === 'found' || entry.state === 'failed',
      };
    });
  });

  constructor() {
    inject(DestroyRef).onDestroy(() => this.cancelClick());
  }

  protected href(id: string): string {
    return this.router.serializeUrl(this.router.createUrlTree(this.path(id)));
  }

  /**
   * Click on a chip link in a table cell: modified clicks (new tab, …) are the browser's; a plain
   * click navigates after {@link CHIP_CLICK_DELAY_MS} unless a second click follows, whose
   * double click then reaches the cell and opens its editor.
   */
  protected onCellClick(event: MouseEvent, id: string): void {
    if (event.button !== 0 || event.ctrlKey || event.metaKey || event.shiftKey || event.altKey) {
      return;
    }
    event.preventDefault();
    this.cancelClick();
    if (event.detail > 1) {
      return; // part of a double click
    }
    this.clickTimer = setTimeout(() => {
      this.clickTimer = null;
      void this.router.navigate(this.path(id));
    }, this.clickDelay);
  }

  private path(id: string): string[] {
    return ['/w', this.slug(), 'records', id];
  }

  private cancelClick(): void {
    if (this.clickTimer !== null) {
      clearTimeout(this.clickTimer);
      this.clickTimer = null;
    }
  }

  protected readonly hidden = computed(() => {
    const max = this.max();
    return max === null ? 0 : Math.max(0, this.ids().length - max);
  });
}
