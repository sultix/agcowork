import { ChangeDetectionStrategy, Component, computed, inject, input, signal } from '@angular/core';
import { NgClass } from '@angular/common';
import { Router, RouterLink } from '@angular/router';
import { NgIcon, provideIcons } from '@ng-icons/core';
import {
  lucideBraces,
  lucideCircleAlert,
  lucideFileText,
  lucideKanban,
  lucideLayoutGrid,
  lucideList,
  lucidePlus,
  lucideStar,
  lucideTable,
} from '@ng-icons/lucide';
import { HlmAlertImports } from '@spartan-ng/helm/alert';
import { HlmButton } from '@spartan-ng/helm/button';
import { HlmInput } from '@spartan-ng/helm/input';
import { HlmLabel } from '@spartan-ng/helm/label';
import { WorkspaceContext } from '../../core/workspace/workspace-context.service';
import { describeError } from '../../shared/ui/api-error-text';
import { ConfirmDialogComponent } from '../../shared/ui/confirm-dialog.component';
import {
  CREATABLE_VIEW_TYPES,
  CollectionStore,
  VIEW_TYPE_ICONS,
  VIEW_TYPE_LABELS,
  ViewDto,
} from './collection.store';

type Panel = 'none' | 'create' | 'rename';

/**
 * Tabs with the views of a collection plus the actions on them: create (name, type, grouping for
 * boards), rename, make default, delete (not for system views). Actions need `write`.
 */
@Component({
  selector: 'ag-view-switcher',
  imports: [
    ConfirmDialogComponent,
    HlmAlertImports,
    HlmButton,
    HlmInput,
    HlmLabel,
    NgClass,
    NgIcon,
    RouterLink,
  ],
  providers: [
    provideIcons({
      lucideCircleAlert,
      lucidePlus,
      lucideStar,
      lucideTable,
      lucideKanban,
      lucideList,
      lucideLayoutGrid,
      lucideBraces,
      lucideFileText,
    }),
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="flex flex-wrap items-center gap-2" data-testid="view-switcher">
      <nav
        class="inline-flex w-fit flex-wrap items-center gap-1 rounded-lg bg-muted p-[3px] text-muted-foreground"
        aria-label="Views"
      >
        @for (view of store.views(); track view.id) {
          <a
            class="inline-flex h-8 items-center gap-1.5 rounded-md border border-transparent px-2.5 text-sm font-medium whitespace-nowrap outline-none transition-all hover:text-foreground focus-visible:border-ring focus-visible:ring-3 focus-visible:ring-ring/50"
            data-testid="view-tab"
            [attr.data-id]="view.id"
            [attr.aria-current]="view.id === activeId() ? 'page' : null"
            [ngClass]="
              view.id === activeId()
                ? 'bg-background text-foreground shadow-sm dark:border-input dark:bg-input/30'
                : ''
            "
            [routerLink]="link(view)"
          >
            <ng-icon aria-hidden="true" [name]="icon(view)" [attr.data-icon]="icon(view)" />
            {{ view.name }}
            @if (view.isDefault) {
              <ng-icon
                name="lucideStar"
                class="text-xs"
                data-testid="view-default-mark"
                title="Standard-View"
              />
              <span class="sr-only">(Standard)</span>
            }
          </a>
        }
      </nav>
      @if (store.canWrite()) {
        <button
          type="button"
          hlmBtn
          variant="ghost"
          size="sm"
          data-testid="view-new"
          (click)="openCreate()"
        >
          <ng-icon name="lucidePlus" />
          Neue View
        </button>
        @if (active(); as view) {
          <div class="ml-auto flex gap-1">
            <button
              type="button"
              hlmBtn
              variant="ghost"
              size="sm"
              data-testid="view-rename"
              (click)="openRename(view)"
            >
              Umbenennen
            </button>
            @if (!view.isDefault) {
              <button
                type="button"
                hlmBtn
                variant="ghost"
                size="sm"
                data-testid="view-make-default"
                [disabled]="busy()"
                (click)="makeDefault(view)"
              >
                Als Standard
              </button>
            }
            <button
              type="button"
              hlmBtn
              variant="ghost"
              size="sm"
              class="hover:text-destructive"
              data-testid="view-delete"
              [disabled]="view.isSystem || busy()"
              [attr.title]="view.isSystem ? 'System-Views können nicht gelöscht werden.' : null"
              (click)="deleting.set(view)"
            >
              Löschen
            </button>
          </div>
        }
      }
    </div>

    @if (panel() === 'create') {
      <form
        class="mt-2 flex flex-wrap items-end gap-3 rounded-xl border bg-card p-3 text-sm text-card-foreground"
        data-testid="view-create-form"
        (submit)="$event.preventDefault(); create()"
      >
        <label hlmLabel class="flex-col items-start gap-2">
          Name
          <input
            hlmInput
            class="w-48"
            data-testid="view-create-name"
            required
            maxlength="100"
            [value]="name()"
            (input)="name.set($any($event.target).value)"
          />
        </label>
        <label hlmLabel class="flex-col items-start gap-2">
          Typ
          <select
            hlmInput
            class="w-auto"
            data-testid="view-create-type"
            (change)="type.set($any($event.target).value)"
          >
            @for (t of types; track t) {
              <option [value]="t" [selected]="t === type()">{{ typeLabel(t) }}</option>
            }
          </select>
        </label>
        @if (type() === 'board') {
          <label hlmLabel class="flex-col items-start gap-2">
            Gruppieren nach
            <select
              hlmInput
              class="w-auto"
              data-testid="view-create-group"
              (change)="groupBy.set($any($event.target).value)"
            >
              @for (choice of groupChoices(); track choice.key) {
                <option [value]="choice.key" [selected]="choice.key === groupBy()">
                  {{ choice.name }}
                </option>
              }
            </select>
          </label>
        }
        @if (type() === 'list' || type() === 'gallery') {
          <p class="w-full text-xs text-muted-foreground">
            {{ typeLabel(type()) }} wird vorerst als Tabelle dargestellt.
          </p>
        }
        <button
          type="submit"
          hlmBtn
          data-testid="view-create-submit"
          [disabled]="busy() || name().trim() === '' || (type() === 'board' && !groupBy())"
        >
          Anlegen
        </button>
        <button type="button" hlmBtn variant="outline" (click)="panel.set('none')">
          Abbrechen
        </button>
      </form>
    }
    @if (panel() === 'rename') {
      <form
        class="mt-2 flex items-end gap-3 rounded-xl border bg-card p-3 text-sm text-card-foreground"
        data-testid="view-rename-form"
        (submit)="$event.preventDefault(); rename()"
      >
        <label hlmLabel class="flex-col items-start gap-2">
          Neuer Name
          <input
            hlmInput
            class="w-48"
            data-testid="view-rename-name"
            required
            maxlength="100"
            [value]="name()"
            (input)="name.set($any($event.target).value)"
          />
        </label>
        <button
          type="submit"
          hlmBtn
          data-testid="view-rename-submit"
          [disabled]="busy() || name().trim() === ''"
        >
          Umbenennen
        </button>
        <button type="button" hlmBtn variant="outline" (click)="panel.set('none')">
          Abbrechen
        </button>
      </form>
    }
    @if (error(); as message) {
      <div hlmAlert variant="destructive" class="mt-2" data-testid="view-action-error">
        <ng-icon name="lucideCircleAlert" />
        <p hlmAlertDescription>{{ message }}</p>
      </div>
    }

    <ag-confirm-dialog
      [open]="deleting() !== null"
      title="View löschen?"
      [message]="
        'Die View „' + (deleting()?.name ?? '') + '“ wird gelöscht. Die Records bleiben erhalten.'
      "
      confirmLabel="Löschen"
      [busy]="busy()"
      (confirmed)="remove()"
      (cancelled)="deleting.set(null)"
    />
  `,
})
export class ViewSwitcherComponent {
  protected readonly store = inject(CollectionStore);
  private readonly context = inject(WorkspaceContext);
  private readonly router = inject(Router);

  /** Id of the view being shown. */
  readonly activeId = input<string | null>(null);

  protected readonly types = CREATABLE_VIEW_TYPES;
  protected readonly panel = signal<Panel>('none');
  protected readonly name = signal('');
  protected readonly type = signal<string>('table');
  protected readonly groupBy = signal('');
  protected readonly busy = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly deleting = signal<ViewDto | null>(null);

  protected readonly active = computed(() => this.store.find(this.activeId()));
  protected readonly groupChoices = computed(() =>
    this.store.schema().filter((d) => d.type === 'select' || d.type === 'status'),
  );

  protected link(view: ViewDto): unknown[] {
    return ['/w', this.context.slug(), 'collections', view.collectionId, 'views', view.id];
  }

  protected icon(view: ViewDto): string {
    return VIEW_TYPE_ICONS[view.type] ?? 'lucideTable';
  }

  protected typeLabel(type: string): string {
    return VIEW_TYPE_LABELS[type] ?? type;
  }

  protected openCreate(): void {
    this.error.set(null);
    this.name.set('');
    this.type.set('table');
    const status = this.groupChoices().find((d) => d.type === 'status') ?? this.groupChoices()[0];
    this.groupBy.set(status?.key ?? '');
    this.panel.set('create');
  }

  protected openRename(view: ViewDto): void {
    this.error.set(null);
    this.name.set(view.name);
    this.panel.set('rename');
  }

  protected async create(): Promise<void> {
    await this.run(async () => {
      const type = this.type();
      const view = await this.store.create({
        name: this.name().trim(),
        type,
        ...(type === 'board' ? { groupBy: this.groupBy() } : {}),
      });
      this.panel.set('none');
      await this.router.navigate(this.link(view));
    });
  }

  protected async rename(): Promise<void> {
    const view = this.active();
    if (!view) {
      return;
    }
    await this.run(async () => {
      await this.store.update(view.id, { name: this.name().trim() });
      this.panel.set('none');
    });
  }

  protected async makeDefault(view: ViewDto): Promise<void> {
    await this.run(() => this.store.setDefault(view.id));
  }

  protected async remove(): Promise<void> {
    const view = this.deleting();
    if (!view || view.isSystem) {
      this.deleting.set(null);
      return;
    }
    await this.run(async () => {
      await this.store.remove(view.id);
      this.deleting.set(null);
      if (view.id === this.activeId()) {
        await this.router.navigate(['/w', this.context.slug(), 'collections', view.collectionId], {
          replaceUrl: true,
        });
      }
    });
    this.deleting.set(null);
  }

  private async run(action: () => Promise<unknown>): Promise<void> {
    this.busy.set(true);
    this.error.set(null);
    try {
      await action();
    } catch (error) {
      this.error.set(describeError(error));
    } finally {
      this.busy.set(false);
    }
  }
}
