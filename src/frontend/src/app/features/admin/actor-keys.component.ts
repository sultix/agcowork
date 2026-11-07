import { DatePipe } from '@angular/common';
import {
  ChangeDetectionStrategy,
  Component,
  computed,
  inject,
  input,
  linkedSignal,
  resource,
  signal,
} from '@angular/core';
import { RouterLink } from '@angular/router';
import { NgIcon, provideIcons } from '@ng-icons/core';
import { lucideArrowLeft, lucideCircleAlert, lucideCopy, lucideInfo } from '@ng-icons/lucide';
import { HlmAlertImports } from '@spartan-ng/helm/alert';
import { HlmButton } from '@spartan-ng/helm/button';
import { HlmCardImports } from '@spartan-ng/helm/card';
import { HlmCheckbox } from '@spartan-ng/helm/checkbox';
import { HlmFieldImports } from '@spartan-ng/helm/field';
import { HlmInput } from '@spartan-ng/helm/input';
import { HlmLabel } from '@spartan-ng/helm/label';
import { HlmNativeSelectImports } from '@spartan-ng/helm/native-select';
import { HlmSkeleton } from '@spartan-ng/helm/skeleton';
import { HlmTableImports } from '@spartan-ng/helm/table';
import { firstValueFrom } from 'rxjs';
import { ApiClient } from '../../core/api/api-client';
import type { components } from '../../core/api/generated/schema';
import { describeError } from '../../shared/ui/api-error-text';
import { BadgeComponent } from '../../shared/ui/badge.component';
import { ConfirmDialogComponent } from '../../shared/ui/confirm-dialog.component';
import { WorkspacesStore } from '../workspaces/workspaces.store';

type Actor = components['schemas']['ActorDto'];
type ApiKey = components['schemas']['ApiKeyDto'];

const KEY_SCOPES = ['read', 'write', 'schema', 'admin'] as const;

/** `datetime-local` value (local time) → ISO timestamp; `null` when empty or invalid. */
export function toIsoTimestamp(local: string): string | null {
  if (!local) {
    return null;
  }
  const date = new Date(local);
  return Number.isNaN(date.getTime()) ? null : date.toISOString();
}

/**
 * `/admin/actors/:id/keys`: API keys of an agent. The plain-text key of a new key is held in
 * memory only, shown once and gone when dismissed or when the page is left.
 */
@Component({
  selector: 'ag-actor-keys',
  imports: [
    DatePipe,
    RouterLink,
    NgIcon,
    HlmAlertImports,
    HlmButton,
    HlmCardImports,
    HlmCheckbox,
    HlmFieldImports,
    HlmInput,
    HlmLabel,
    HlmNativeSelectImports,
    HlmSkeleton,
    HlmTableImports,
    BadgeComponent,
    ConfirmDialogComponent,
  ],
  providers: [provideIcons({ lucideArrowLeft, lucideCircleAlert, lucideCopy, lucideInfo })],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <section class="mx-auto flex max-w-5xl flex-col gap-6 p-6">
      <div class="flex flex-col gap-2">
        <a hlmBtn variant="ghost" size="sm" routerLink="/admin/actors" class="self-start">
          <ng-icon name="lucideArrowLeft" />
          Actors
        </a>
        <h1 class="text-2xl font-semibold tracking-tight">
          API-Keys
          @if (actor(); as a) {
            <span class="text-muted-foreground" data-testid="actor-name">· {{ a.name }}</span>
          }
        </h1>
      </div>

      @if (actionError(); as message) {
        <div hlmAlert variant="destructive" data-testid="action-error">
          <ng-icon name="lucideCircleAlert" />
          <p hlmAlertDescription class="whitespace-pre-line">{{ message }}</p>
        </div>
      }

      @if (createdKey(); as key) {
        <section hlmCard class="ring-2 ring-primary" role="status" data-testid="created-key">
          <header hlmCardHeader>
            <h2 hlmCardTitle>Neuer Key</h2>
            <p hlmCardDescription class="text-destructive">
              Dieser Key wird nur jetzt angezeigt. Kopiere ihn und bewahre ihn sicher auf; er lässt
              sich später nicht mehr abrufen.
            </p>
          </header>
          <div hlmCardContent class="flex flex-col gap-4">
            <code
              class="block rounded-md border bg-muted px-3 py-2 font-mono text-sm break-all"
              data-testid="created-key-value"
              >{{ key }}</code
            >
            <div class="flex flex-wrap items-center gap-3">
              <button hlmBtn type="button" data-testid="copy-key" (click)="copyKey()">
                <ng-icon name="lucideCopy" />
                Kopieren
              </button>
              @if (copyState(); as state) {
                <span class="text-sm text-muted-foreground" data-testid="copy-state">{{
                  state
                }}</span>
              }
              <button
                hlmBtn
                type="button"
                variant="outline"
                class="ml-auto"
                data-testid="dismiss-key"
                (click)="dismissKey()"
              >
                Fertig, Key ausblenden
              </button>
            </div>
          </div>
        </section>
      }

      @if (actor(); as a) {
        @if (a.kind === 'agent') {
          <form hlmCard novalidate data-testid="create-form" (submit)="onCreate($event)">
            <header hlmCardHeader>
              <h2 hlmCardTitle>Key erzeugen</h2>
            </header>
            <div hlmCardContent>
              <div hlmFieldGroup class="gap-5">
                @if (formError(); as message) {
                  <div hlmAlert variant="destructive" data-testid="form-error">
                    <ng-icon name="lucideCircleAlert" />
                    <p hlmAlertDescription class="whitespace-pre-line">{{ message }}</p>
                  </div>
                }
                <fieldset hlmFieldSet class="gap-2">
                  <legend hlmFieldLegend variant="label" class="mb-0">
                    Scopes (optional, leer = Rechte der Mitgliedschaft)
                  </legend>
                  <div class="flex flex-wrap gap-x-4 gap-y-2">
                    @for (scope of keyScopes; track scope) {
                      <div class="flex items-center gap-2">
                        <hlm-checkbox
                          [inputId]="'key-scope-' + scope"
                          [attr.data-testid]="'key-scope-' + scope"
                          [checked]="scopes().includes(scope)"
                          (checkedChange)="toggleScope(scope)"
                        />
                        <label hlmLabel class="font-normal" [for]="'key-scope-' + scope">{{
                          scope
                        }}</label>
                      </div>
                    }
                  </div>
                </fieldset>

                <div hlmField class="gap-2">
                  <label hlmFieldLabel for="key-workspace"
                    >Auf einen Workspace beschränken (optional)</label
                  >
                  <hlm-native-select
                    selectId="key-workspace"
                    class="w-full"
                    data-testid="key-workspace"
                    (valueChange)="workspaceId.set($any($event))"
                  >
                    <option hlmNativeSelectOption value="">Alle Workspaces</option>
                    @for (ws of workspaces.workspaces(); track ws.id) {
                      <option hlmNativeSelectOption [value]="ws.id">
                        {{ ws.name }} ({{ ws.slug }})
                      </option>
                    }
                  </hlm-native-select>
                </div>

                <div hlmField class="gap-2">
                  <label hlmFieldLabel for="key-expires">Läuft ab am (optional)</label>
                  <input
                    hlmInput
                    id="key-expires"
                    type="datetime-local"
                    class="w-auto self-start"
                    data-testid="key-expires"
                    (input)="expiresAt.set($any($event.target).value)"
                  />
                </div>

                <div class="flex justify-end">
                  <button hlmBtn type="submit" data-testid="create-key" [disabled]="creating()">
                    Key erzeugen
                  </button>
                </div>
              </div>
            </div>
          </form>
        } @else {
          <div hlmAlert role="status" data-testid="human-hint">
            <ng-icon name="lucideInfo" />
            <p hlmAlertDescription>
              Menschen melden sich mit Passwort an; API-Keys gibt es nur für Agenten.
            </p>
          </div>
        }
      }

      @if (loadError(); as message) {
        <div hlmAlert variant="destructive" data-testid="load-error">
          <ng-icon name="lucideCircleAlert" />
          <p hlmAlertDescription>{{ message }}</p>
        </div>
      } @else if (loading()) {
        <div class="flex flex-col gap-2" aria-busy="true" aria-label="Wird geladen">
          <hlm-skeleton class="h-10 rounded-lg" />
          <hlm-skeleton class="h-10 rounded-lg" />
        </div>
      } @else {
        <div class="rounded-xl border">
          <div hlmTableContainer>
            <table hlmTable data-testid="keys-table">
              <thead hlmTHead>
                <tr hlmTr>
                  <th hlmTh class="px-4">Prefix</th>
                  <th hlmTh>Scopes</th>
                  <th hlmTh>Workspace</th>
                  <th hlmTh>Erstellt</th>
                  <th hlmTh>Zuletzt benutzt</th>
                  <th hlmTh>Status</th>
                  <th hlmTh class="w-12"><span class="sr-only">Aktionen</span></th>
                </tr>
              </thead>
              <tbody hlmTBody>
                @for (key of keys(); track key.id) {
                  <tr hlmTr data-testid="key-row">
                    <td hlmTd class="px-4 font-mono">{{ key.prefix }}</td>
                    <td hlmTd class="text-muted-foreground">
                      {{ key.scopes.length ? key.scopes.join(', ') : 'wie Mitgliedschaft' }}
                    </td>
                    <td hlmTd>{{ workspaceLabel(key.workspaceId) }}</td>
                    <td hlmTd>{{ key.createdAt | date: 'dd.MM.yyyy HH:mm' }}</td>
                    <td hlmTd>
                      {{ key.lastUsedAt ? (key.lastUsedAt | date: 'dd.MM.yyyy HH:mm') : 'nie' }}
                    </td>
                    <td hlmTd>
                      @switch (statusOf(key)) {
                        @case ('revoked') {
                          <ag-badge tone="danger">widerrufen</ag-badge>
                        }
                        @case ('expired') {
                          <ag-badge tone="danger">abgelaufen</ag-badge>
                        }
                        @default {
                          <ag-badge tone="success">aktiv</ag-badge>
                          @if (key.expiresAt) {
                            <span class="ml-1 text-muted-foreground"
                              >bis {{ key.expiresAt | date: 'dd.MM.yyyy HH:mm' }}</span
                            >
                          }
                        }
                      }
                    </td>
                    <td hlmTd class="text-right">
                      @if (statusOf(key) === 'active') {
                        <button
                          hlmBtn
                          type="button"
                          variant="destructive"
                          size="sm"
                          data-testid="revoke-key"
                          (click)="revoking.set(key)"
                        >
                          Widerrufen
                        </button>
                      }
                    </td>
                  </tr>
                } @empty {
                  <tr hlmTr>
                    <td hlmTd colspan="7" class="px-4 text-muted-foreground">Noch keine Keys.</td>
                  </tr>
                }
              </tbody>
            </table>
          </div>
        </div>
      }
    </section>

    <ag-confirm-dialog
      [open]="revoking() !== null"
      title="Key widerrufen?"
      message="Der Key wird sofort ungültig; Anfragen damit werden mit 401 abgelehnt."
      confirmLabel="Widerrufen"
      [busy]="busy()"
      (confirmed)="confirmRevoke()"
      (cancelled)="revoking.set(null)"
    />
  `,
})
export class ActorKeysComponent {
  private readonly api = inject(ApiClient);
  protected readonly workspaces = inject(WorkspacesStore);

  /** Route parameter `:id` (router input binding). */
  readonly id = input.required<string>();

  protected readonly keyScopes = KEY_SCOPES;

  private readonly actorResource = resource<Actor, string>({
    params: () => this.id(),
    loader: ({ params: id }) => firstValueFrom(this.api.get('/actors/{id}', { path: { id } })),
  });
  protected readonly actor = computed(() =>
    this.actorResource.hasValue() ? this.actorResource.value() : null,
  );

  private readonly keysResource = resource<ApiKey[], string>({
    params: () => this.id(),
    loader: ({ params: id }) => firstValueFrom(this.api.get('/actors/{id}/keys', { path: { id } })),
  });
  protected readonly keys = computed(() =>
    this.keysResource.hasValue() ? this.keysResource.value() : [],
  );
  protected readonly loading = computed(
    () => this.keysResource.isLoading() && !this.keysResource.hasValue(),
  );
  protected readonly loadError = computed(() =>
    this.keysResource.error() ? describeError(this.keysResource.error()) : null,
  );

  protected readonly scopes = signal<readonly string[]>([]);
  protected readonly workspaceId = signal('');
  protected readonly expiresAt = signal('');
  protected readonly creating = signal(false);
  protected readonly formError = signal<string | null>(null);
  protected readonly actionError = signal<string | null>(null);

  /** Plain-text key of the key just created; memory only, never persisted or logged. */
  protected readonly createdKey = linkedSignal<string, string | null>({
    source: this.id,
    computation: () => null,
  });
  protected readonly copyState = linkedSignal<string, string | null>({
    source: this.id,
    computation: () => null,
  });

  protected readonly revoking = signal<ApiKey | null>(null);
  protected readonly busy = signal(false);

  protected toggleScope(scope: string): void {
    this.scopes.update((list) =>
      list.includes(scope) ? list.filter((s) => s !== scope) : [...list, scope],
    );
  }

  protected workspaceLabel(workspaceId: string | null): string {
    if (!workspaceId) {
      return 'alle';
    }
    const ws = this.workspaces.workspaces().find((w) => w.id === workspaceId);
    return ws ? ws.name : workspaceId;
  }

  protected statusOf(key: ApiKey): 'active' | 'revoked' | 'expired' {
    if (key.revokedAt) {
      return 'revoked';
    }
    return key.expiresAt && new Date(key.expiresAt).getTime() <= Date.now() ? 'expired' : 'active';
  }

  protected async onCreate(event: Event): Promise<void> {
    event.preventDefault();
    this.formError.set(null);
    this.actionError.set(null);
    this.creating.set(true);
    try {
      const created = await firstValueFrom(
        this.api.post('/actors/{id}/keys', {
          path: { id: this.id() },
          body: {
            scopes: [...this.scopes()],
            workspaceId: this.workspaceId() || null,
            expiresAt: toIsoTimestamp(this.expiresAt()),
          },
        }),
      );
      this.createdKey.set(created.key);
      this.copyState.set(null);
      this.scopes.set([]);
      this.keysResource.reload();
    } catch (error) {
      this.formError.set(describeError(error));
    } finally {
      this.creating.set(false);
    }
  }

  protected async copyKey(): Promise<void> {
    const key = this.createdKey();
    if (!key) {
      return;
    }
    try {
      await navigator.clipboard.writeText(key);
      this.copyState.set('Kopiert.');
    } catch {
      this.copyState.set('Kopieren nicht möglich. Bitte den Key manuell markieren und kopieren.');
    }
  }

  protected dismissKey(): void {
    this.createdKey.set(null);
    this.copyState.set(null);
  }

  protected async confirmRevoke(): Promise<void> {
    const key = this.revoking();
    if (!key) {
      return;
    }
    this.busy.set(true);
    this.actionError.set(null);
    try {
      await firstValueFrom(this.api.delete('/keys/{id}', { path: { id: key.id } }), {
        defaultValue: undefined,
      });
      this.keysResource.reload();
    } catch (error) {
      this.actionError.set(describeError(error));
    } finally {
      this.busy.set(false);
      this.revoking.set(null);
    }
  }
}
