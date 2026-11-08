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
import { NgIcon, provideIcons } from '@ng-icons/core';
import {
  lucideCircleAlert,
  lucideCircleCheck,
  lucideEllipsis,
  lucideInfo,
  lucidePencil,
  lucidePlus,
  lucideTrash2,
} from '@ng-icons/lucide';
import { HlmAlertImports } from '@spartan-ng/helm/alert';
import { HlmButton } from '@spartan-ng/helm/button';
import { HlmDropdownMenuImports } from '@spartan-ng/helm/dropdown-menu';
import { HlmSkeleton } from '@spartan-ng/helm/skeleton';
import { HlmTableImports } from '@spartan-ng/helm/table';
import { ApiError } from '../../../core/api/api-error';
import { WorkspaceContext } from '../../../core/workspace/workspace-context.service';
import { optionSwatch, propertyTypeLabel } from '../../../shared/properties/property-labels';
import type { PropertyDefinition } from '../../../shared/properties/property-types';
import { BadgeComponent } from '../../../shared/ui/badge.component';
import { BreadcrumbsComponent, Crumb } from '../../../shared/ui/breadcrumbs.component';
import { ConfirmDialogComponent } from '../../../shared/ui/confirm-dialog.component';
import { WorkspaceTreeStore } from '../../spaces/workspace-tree.store';
import { PropertyDialogComponent } from './property-dialog.component';
import { AffectedCount, TITLE_KEY, describeLoss, isComputed } from './schema-draft';
import { SCHEMA_VERSION_CONFLICT, describeSchemaError } from './schema-errors';
import { SchemaEditorStore } from './schema-editor.store';

/** The open property dialog: which property (`null` = new) against which schema version. */
interface OpenDialog {
  readonly original: PropertyDefinition | null;
  readonly version: number;
}

/** A pending removal: the property, the version it was seen in and the affected records. */
interface Removal {
  readonly property: PropertyDefinition;
  readonly version: number;
  readonly count: AffectedCount;
}

/**
 * `/w/:slug/settings/collections/:collectionId/schema`: the properties of a collection in schema
 * order. With the `schema` scope (active workspace and collection) properties are added, edited
 * ({@link PropertyDialogComponent}) and removed (after a confirmation naming the affected
 * records); without it the page is read-only. Changes by others (`collection.schema_changed`)
 * show up live; an open dialog keeps its input.
 */
@Component({
  selector: 'ag-schema-editor-page',
  imports: [
    NgIcon,
    HlmAlertImports,
    HlmButton,
    HlmDropdownMenuImports,
    HlmSkeleton,
    HlmTableImports,
    BadgeComponent,
    BreadcrumbsComponent,
    ConfirmDialogComponent,
    PropertyDialogComponent,
  ],
  providers: [
    SchemaEditorStore,
    provideIcons({
      lucideCircleAlert,
      lucideCircleCheck,
      lucideEllipsis,
      lucideInfo,
      lucidePencil,
      lucidePlus,
      lucideTrash2,
    }),
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <section class="mx-auto flex max-w-6xl flex-col gap-6 p-6">
      @if (store.loadError(); as message) {
        <div hlmAlert variant="destructive" data-testid="load-error">
          <ng-icon name="lucideCircleAlert" />
          <p hlmAlertDescription>{{ message }}</p>
        </div>
      } @else if (store.collection(); as collection) {
        <ag-breadcrumbs [crumbs]="crumbs()" />
        <div class="flex flex-wrap items-start justify-between gap-4">
          <div class="flex flex-col gap-1">
            <h1 class="text-2xl font-semibold tracking-tight" data-testid="schema-title">
              Schema: {{ collection.name }}
            </h1>
            <p class="text-sm text-muted-foreground">
              Properties in der Reihenfolge des Schemas. Agenten lesen Keys, Typen und
              Beschreibungen, um Records richtig zu füllen.
            </p>
          </div>
          @if (store.canEdit()) {
            <button hlmBtn type="button" data-testid="property-new" (click)="openCreate()">
              <ng-icon name="lucidePlus" />
              Property hinzufügen
            </button>
          }
        </div>

        @if (store.readOnlyReason(); as reason) {
          <div hlmAlert role="status" data-testid="readonly-notice">
            <ng-icon name="lucideInfo" />
            <p hlmAlertDescription>{{ reason }}</p>
          </div>
        }
        @if (notice(); as message) {
          <div hlmAlert role="status" data-testid="notice">
            <ng-icon name="lucideCircleCheck" />
            <p hlmAlertDescription>{{ message }}</p>
          </div>
        }
        @if (actionError(); as message) {
          <div hlmAlert variant="destructive" data-testid="action-error">
            <ng-icon name="lucideCircleAlert" />
            <p hlmAlertDescription class="whitespace-pre-line">{{ message }}</p>
          </div>
        }

        <div class="rounded-xl border">
          <div hlmTableContainer>
            <table hlmTable data-testid="schema-table">
              <caption class="sr-only">
                Properties der Collection
                {{
                  collection.name
                }}
              </caption>
              <thead hlmTHead>
                <tr hlmTr>
                  <th hlmTh class="px-4">Name</th>
                  <th hlmTh>Key</th>
                  <th hlmTh>Typ</th>
                  <th hlmTh>Pflicht</th>
                  <th hlmTh>Beschreibung</th>
                  @if (store.canEdit()) {
                    <th hlmTh class="w-12"><span class="sr-only">Aktionen</span></th>
                  }
                </tr>
              </thead>
              <tbody hlmTBody>
                @for (property of store.schema(); track property.key) {
                  <tr hlmTr data-testid="property-row" [attr.data-key]="property.key">
                    <td hlmTd class="px-4 align-top font-medium whitespace-normal">
                      {{ property.name }}
                    </td>
                    <td hlmTd class="align-top font-mono text-xs">{{ property.key }}</td>
                    <td hlmTd class="align-top whitespace-normal">
                      <div class="flex flex-col items-start gap-1">
                        <ag-badge data-testid="property-type-label">{{
                          typeLabel(property.type)
                        }}</ag-badge>
                        @for (detail of details(property); track $index) {
                          <span
                            class="text-xs text-muted-foreground"
                            data-testid="property-detail"
                            >{{ detail }}</span
                          >
                        }
                        @if (property.options?.length) {
                          <ul
                            class="flex flex-wrap gap-1"
                            [attr.aria-label]="'Optionen von ' + property.name"
                          >
                            @for (option of property.options; track option.value) {
                              <li
                                class="inline-flex items-center gap-1 rounded-md border px-1.5 py-0.5 text-xs"
                              >
                                @if (swatch(option.color); as swatchClass) {
                                  <span
                                    aria-hidden="true"
                                    class="size-2 rounded-full"
                                    [class]="swatchClass"
                                  ></span>
                                }
                                {{ option.label }}
                              </li>
                            }
                          </ul>
                        }
                      </div>
                    </td>
                    <td hlmTd class="align-top">
                      @if (property.required) {
                        Ja
                      } @else {
                        <span class="text-muted-foreground">Nein</span>
                      }
                    </td>
                    <td hlmTd class="max-w-md align-top whitespace-normal text-muted-foreground">
                      {{ property.description || '–' }}
                    </td>
                    @if (store.canEdit()) {
                      <td hlmTd class="text-right align-top">
                        <button
                          hlmBtn
                          type="button"
                          variant="ghost"
                          size="icon-sm"
                          data-testid="property-actions"
                          [attr.aria-label]="'Aktionen für ' + property.name"
                          [hlmDropdownMenuTrigger]="actionMenu"
                          [hlmDropdownMenuTriggerData]="{ property }"
                          align="end"
                        >
                          <ng-icon name="lucideEllipsis" />
                        </button>
                      </td>
                    }
                  </tr>
                }
              </tbody>
            </table>
          </div>
        </div>
      } @else {
        <div class="flex flex-col gap-3" aria-busy="true" aria-label="Wird geladen">
          <hlm-skeleton class="h-4 w-48" />
          <hlm-skeleton class="h-8 w-72" />
          <hlm-skeleton class="h-10 rounded-lg" />
          <hlm-skeleton class="h-10 rounded-lg" />
        </div>
      }
    </section>

    <ng-template #actionMenu let-property="property">
      <hlm-dropdown-menu class="w-44" data-testid="property-menu">
        <button hlmDropdownMenuItem data-testid="property-edit" (triggered)="openEdit(property)">
          <ng-icon name="lucidePencil" />
          Bearbeiten
        </button>
        @if (property.key !== titleKey) {
          <hlm-dropdown-menu-separator />
          <button
            hlmDropdownMenuItem
            variant="destructive"
            data-testid="property-remove"
            (triggered)="askRemove(property)"
          >
            <ng-icon name="lucideTrash2" />
            Entfernen
          </button>
        }
      </hlm-dropdown-menu>
    </ng-template>

    @if (dialog(); as open) {
      <ag-property-dialog
        [original]="open.original"
        [baseVersion]="open.version"
        (saved)="onSaved($event)"
        (cancelled)="dialog.set(null)"
      />
    }

    <ag-confirm-dialog
      [open]="removal() !== null"
      title="Property entfernen?"
      [message]="removeMessage()"
      confirmLabel="Entfernen"
      [busy]="busy()"
      (confirmed)="confirmRemove()"
      (cancelled)="removal.set(null)"
    />
  `,
})
export class SchemaEditorPageComponent {
  protected readonly store = inject(SchemaEditorStore);
  private readonly context = inject(WorkspaceContext);
  private readonly tree = inject(WorkspaceTreeStore);

  /** Route parameter `:collectionId`. */
  readonly collectionId = input.required<string>();

  protected readonly titleKey = TITLE_KEY;
  protected readonly typeLabel = propertyTypeLabel;
  protected readonly swatch = optionSwatch;

  protected readonly dialog = signal<OpenDialog | null>(null);
  protected readonly removal = signal<Removal | null>(null);
  protected readonly busy = signal(false);
  /** Status line after a change (announced politely). */
  protected readonly notice = signal<string | null>(null);
  protected readonly actionError = signal<string | null>(null);

  protected readonly crumbs = computed<Crumb[]>(() => {
    const collection = this.store.collection();
    const slug = this.context.slug();
    if (!collection || !slug) {
      return [];
    }
    return [
      {
        label: this.tree.findSpace(collection.spaceId)?.name ?? 'Space',
        link: ['/w', slug, 'spaces', collection.spaceId],
      },
      { label: collection.name, link: ['/w', slug, 'collections', collection.id] },
      { label: 'Schema' },
    ];
  });

  protected readonly removeMessage = computed(() => {
    const removal = this.removal();
    return removal
      ? `${describeLoss({ kind: 'removed', property: removal.property }, removal.count)} Agenten, Ansichten und Vorlagen verlieren die Property ebenfalls.`
      : '';
  });

  constructor() {
    effect(() => {
      const id = this.collectionId();
      untracked(() => {
        this.store.connect(id);
        this.dialog.set(null);
        this.removal.set(null);
        this.notice.set(null);
        this.actionError.set(null);
      });
    });
  }

  /** Secondary facts of a property for the type column. */
  protected details(property: PropertyDefinition): string[] {
    const details: string[] = [];
    if (property.type === 'relation') {
      const target = property.targetCollectionId;
      const name =
        target === this.store.collection()?.id
          ? 'diese Collection'
          : (this.tree.findCollection(target)?.name ?? 'archivierte oder unbekannte Collection');
      details.push(`→ ${name}${property.multiple ? ' (mehrere)' : ' (einer)'}`);
    }
    if (property.type === 'number') {
      if (property.unit) {
        details.push(`Einheit: ${property.unit}`);
      }
      if (property.precision != null) {
        details.push(`${property.precision} Nachkommastellen`);
      }
    }
    if (isComputed(property.type)) {
      details.push('berechnet');
    }
    if (property.default != null) {
      details.push(`Standard: ${this.formatDefault(property)}`);
    }
    return details;
  }

  private formatDefault(property: PropertyDefinition): string {
    const value: unknown = property.default;
    const label = (v: unknown) => property.options?.find((o) => o.value === v)?.label ?? String(v);
    if (Array.isArray(value)) {
      return value.map(label).join(', ');
    }
    if (typeof value === 'boolean') {
      return value ? 'an' : 'aus';
    }
    return typeof value === 'object' ? JSON.stringify(value) : label(value);
  }

  protected openCreate(): void {
    this.notice.set(null);
    this.actionError.set(null);
    this.dialog.set({ original: null, version: this.store.schemaVersion() });
  }

  protected openEdit(property: PropertyDefinition): void {
    this.notice.set(null);
    this.actionError.set(null);
    this.dialog.set({ original: property, version: this.store.schemaVersion() });
  }

  protected onSaved(message: string): void {
    this.dialog.set(null);
    this.notice.set(message);
  }

  protected askRemove(property: PropertyDefinition): void {
    this.notice.set(null);
    this.actionError.set(null);
    const removal: Removal = { property, version: this.store.schemaVersion(), count: undefined };
    this.removal.set(removal);
    void this.store.countAffected({ kind: 'removed', property }).then((count) => {
      if (this.removal() === removal) {
        this.removal.set({ ...removal, count });
      }
    });
  }

  protected async confirmRemove(): Promise<void> {
    const removal = this.removal();
    if (!removal || this.busy()) {
      return;
    }
    this.busy.set(true);
    try {
      await this.store.update({
        remove: [removal.property.key],
        confirmRemoval: true,
        expectedSchemaVersion: removal.version,
      });
      this.notice.set(`Property „${removal.property.name}“ entfernt.`);
    } catch (error) {
      if (ApiError.from(error).code === SCHEMA_VERSION_CONFLICT) {
        await this.store.refresh();
        this.actionError.set(
          'Das Schema wurde inzwischen geändert und neu geladen. Nichts wurde entfernt; prüfe den neuen Stand und entferne die Property bei Bedarf erneut.',
        );
      } else {
        this.actionError.set(describeSchemaError(error));
      }
    } finally {
      this.busy.set(false);
      this.removal.set(null);
    }
  }
}
