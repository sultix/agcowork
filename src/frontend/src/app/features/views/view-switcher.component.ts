import { ChangeDetectionStrategy, Component, computed, inject, input, signal } from '@angular/core';
import { Router, RouterLink } from '@angular/router';
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
  imports: [ConfirmDialogComponent, RouterLink],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="flex flex-wrap items-end gap-1 border-b border-border" data-testid="view-switcher">
      <nav class="flex flex-wrap gap-1" role="tablist" aria-label="Views">
        @for (view of store.views(); track view.id) {
          <a
            role="tab"
            class="-mb-px flex items-center gap-1 rounded-t-md border border-transparent px-3 py-1.5 text-sm hover:bg-surface-hover"
            data-testid="view-tab"
            [attr.data-id]="view.id"
            [attr.aria-selected]="view.id === activeId()"
            [class.border-border]="view.id === activeId()"
            [class.border-b-bg]="view.id === activeId()"
            [class.bg-bg]="view.id === activeId()"
            [class.font-medium]="view.id === activeId()"
            [routerLink]="link(view)"
          >
            <span aria-hidden="true" class="text-muted">{{ icon(view) }}</span>
            {{ view.name }}
            @if (view.isDefault) {
              <span class="text-xs text-muted" data-testid="view-default-mark" title="Standard-View"
                >★</span
              >
            }
          </a>
        }
      </nav>
      @if (store.canWrite()) {
        <button
          type="button"
          class="mb-1 ml-1 rounded-md px-2 py-1 text-sm text-accent hover:bg-surface-hover"
          data-testid="view-new"
          (click)="openCreate()"
        >
          + Neue View
        </button>
        @if (active(); as view) {
          <div class="mb-1 ml-auto flex gap-2 text-sm">
            <button
              type="button"
              class="text-muted hover:text-fg"
              data-testid="view-rename"
              (click)="openRename(view)"
            >
              Umbenennen
            </button>
            @if (!view.isDefault) {
              <button
                type="button"
                class="text-muted hover:text-fg"
                data-testid="view-make-default"
                [disabled]="busy()"
                (click)="makeDefault(view)"
              >
                Als Standard
              </button>
            }
            <button
              type="button"
              class="text-muted hover:text-danger disabled:cursor-not-allowed disabled:opacity-50"
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
        class="mt-2 flex flex-wrap items-end gap-2 rounded-md border border-border bg-surface p-3 text-sm"
        data-testid="view-create-form"
        (submit)="$event.preventDefault(); create()"
      >
        <label class="flex flex-col gap-1">
          <span class="text-muted">Name</span>
          <input
            class="rounded-md border border-border bg-bg px-2 py-1"
            data-testid="view-create-name"
            required
            maxlength="100"
            [value]="name()"
            (input)="name.set($any($event.target).value)"
          />
        </label>
        <label class="flex flex-col gap-1">
          <span class="text-muted">Typ</span>
          <select
            class="rounded-md border border-border bg-bg px-2 py-1"
            data-testid="view-create-type"
            (change)="type.set($any($event.target).value)"
          >
            @for (t of types; track t) {
              <option [value]="t" [selected]="t === type()">{{ typeLabel(t) }}</option>
            }
          </select>
        </label>
        @if (type() === 'board') {
          <label class="flex flex-col gap-1">
            <span class="text-muted">Gruppieren nach</span>
            <select
              class="rounded-md border border-border bg-bg px-2 py-1"
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
          <p class="w-full text-xs text-muted">
            {{ typeLabel(type()) }} wird vorerst als Tabelle dargestellt.
          </p>
        }
        <button
          type="submit"
          class="rounded-md bg-accent px-3 py-1.5 font-medium text-accent-fg disabled:opacity-60"
          data-testid="view-create-submit"
          [disabled]="busy() || name().trim() === '' || (type() === 'board' && !groupBy())"
        >
          Anlegen
        </button>
        <button
          type="button"
          class="rounded-md border border-border px-3 py-1.5 hover:bg-surface-hover"
          (click)="panel.set('none')"
        >
          Abbrechen
        </button>
      </form>
    }
    @if (panel() === 'rename') {
      <form
        class="mt-2 flex items-end gap-2 rounded-md border border-border bg-surface p-3 text-sm"
        data-testid="view-rename-form"
        (submit)="$event.preventDefault(); rename()"
      >
        <label class="flex flex-col gap-1">
          <span class="text-muted">Neuer Name</span>
          <input
            class="rounded-md border border-border bg-bg px-2 py-1"
            data-testid="view-rename-name"
            required
            maxlength="100"
            [value]="name()"
            (input)="name.set($any($event.target).value)"
          />
        </label>
        <button
          type="submit"
          class="rounded-md bg-accent px-3 py-1.5 font-medium text-accent-fg disabled:opacity-60"
          data-testid="view-rename-submit"
          [disabled]="busy() || name().trim() === ''"
        >
          Umbenennen
        </button>
        <button
          type="button"
          class="rounded-md border border-border px-3 py-1.5 hover:bg-surface-hover"
          (click)="panel.set('none')"
        >
          Abbrechen
        </button>
      </form>
    }
    @if (error(); as message) {
      <p class="mt-2 text-sm text-danger" role="alert" data-testid="view-action-error">
        {{ message }}
      </p>
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
    return VIEW_TYPE_ICONS[view.type] ?? '▦';
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
