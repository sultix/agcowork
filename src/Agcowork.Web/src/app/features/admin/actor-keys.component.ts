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
  imports: [DatePipe, RouterLink, BadgeComponent, ConfirmDialogComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <section class="mx-auto max-w-5xl p-6">
      <a routerLink="/admin/actors" class="text-sm text-muted hover:text-fg">‹ Actors</a>
      <h1 class="mt-2 text-xl font-semibold">
        API-Keys
        @if (actor(); as a) {
          <span class="text-muted" data-testid="actor-name">· {{ a.name }}</span>
        }
      </h1>

      @if (actionError(); as message) {
        <p
          class="mt-4 rounded-md border border-danger px-3 py-2 text-sm whitespace-pre-line text-danger"
          role="alert"
          data-testid="action-error"
        >
          {{ message }}
        </p>
      }

      @if (createdKey(); as key) {
        <div
          class="mt-4 rounded-lg border border-accent bg-surface p-4"
          role="status"
          data-testid="created-key"
        >
          <p class="text-sm font-semibold">Neuer Key</p>
          <p class="mt-1 text-sm text-danger">
            Dieser Key wird nur jetzt angezeigt. Kopiere ihn und bewahre ihn sicher auf; er lässt
            sich später nicht mehr abrufen.
          </p>
          <code
            class="mt-2 block break-all rounded-md border border-border bg-bg px-3 py-2 font-mono text-sm"
            data-testid="created-key-value"
            >{{ key }}</code
          >
          <div class="mt-3 flex items-center gap-3">
            <button
              type="button"
              class="rounded-md bg-accent px-3 py-1.5 text-sm font-medium text-accent-fg"
              data-testid="copy-key"
              (click)="copyKey()"
            >
              Kopieren
            </button>
            @if (copyState(); as state) {
              <span class="text-sm text-muted" data-testid="copy-state">{{ state }}</span>
            }
            <button
              type="button"
              class="ml-auto rounded-md border border-border px-3 py-1.5 text-sm hover:bg-surface-hover"
              data-testid="dismiss-key"
              (click)="dismissKey()"
            >
              Fertig, Key ausblenden
            </button>
          </div>
        </div>
      }

      @if (actor(); as a) {
        @if (a.kind === 'agent') {
          <form
            class="mt-6 rounded-lg border border-border bg-surface p-6"
            (submit)="onCreate($event)"
            data-testid="create-form"
          >
            <h2 class="font-semibold">Key erzeugen</h2>
            @if (formError(); as message) {
              <p
                class="mt-3 text-sm whitespace-pre-line text-danger"
                role="alert"
                data-testid="form-error"
              >
                {{ message }}
              </p>
            }
            <fieldset class="mt-3">
              <legend class="text-sm font-medium">
                Scopes (optional, leer = Rechte der Mitgliedschaft)
              </legend>
              @for (scope of keyScopes; track scope) {
                <label class="mr-3 inline-flex items-center gap-1 text-sm">
                  <input
                    type="checkbox"
                    [attr.data-testid]="'key-scope-' + scope"
                    [checked]="scopes().includes(scope)"
                    (change)="toggleScope(scope)"
                  />{{ scope }}
                </label>
              }
            </fieldset>

            <label for="key-workspace" class="mt-3 block text-sm font-medium"
              >Auf einen Workspace beschränken (optional)</label
            >
            <select
              id="key-workspace"
              class="mt-1 w-full rounded-md border border-border bg-bg px-3 py-2 text-sm"
              data-testid="key-workspace"
              (change)="workspaceId.set($any($event.target).value)"
            >
              <option value="">Alle Workspaces</option>
              @for (ws of workspaces.workspaces(); track ws.id) {
                <option [value]="ws.id">{{ ws.name }} ({{ ws.slug }})</option>
              }
            </select>

            <label for="key-expires" class="mt-3 block text-sm font-medium"
              >Läuft ab am (optional)</label
            >
            <input
              id="key-expires"
              type="datetime-local"
              class="mt-1 rounded-md border border-border bg-bg px-3 py-2 text-sm"
              data-testid="key-expires"
              (input)="expiresAt.set($any($event.target).value)"
            />

            <div>
              <button
                type="submit"
                class="mt-4 rounded-md bg-accent px-4 py-2 text-sm font-medium text-accent-fg disabled:opacity-60"
                data-testid="create-key"
                [disabled]="creating()"
              >
                Key erzeugen
              </button>
            </div>
          </form>
        } @else {
          <p class="mt-4 text-sm text-muted" data-testid="human-hint">
            Menschen melden sich mit Passwort an; API-Keys gibt es nur für Agenten.
          </p>
        }
      }

      @if (loadError(); as message) {
        <p class="mt-4 text-sm text-danger" role="alert" data-testid="load-error">{{ message }}</p>
      } @else {
        <table class="mt-6 w-full text-left text-sm" data-testid="keys-table">
          <thead class="border-b border-border text-muted">
            <tr>
              <th class="py-2 pr-3 font-medium">Prefix</th>
              <th class="py-2 pr-3 font-medium">Scopes</th>
              <th class="py-2 pr-3 font-medium">Workspace</th>
              <th class="py-2 pr-3 font-medium">Erstellt</th>
              <th class="py-2 pr-3 font-medium">Zuletzt benutzt</th>
              <th class="py-2 pr-3 font-medium">Status</th>
              <th class="py-2"></th>
            </tr>
          </thead>
          <tbody>
            @for (key of keys(); track key.id) {
              <tr class="border-b border-border" data-testid="key-row">
                <td class="py-2 pr-3 font-mono">{{ key.prefix }}</td>
                <td class="py-2 pr-3 text-muted">
                  {{ key.scopes.length ? key.scopes.join(', ') : 'wie Mitgliedschaft' }}
                </td>
                <td class="py-2 pr-3">{{ workspaceLabel(key.workspaceId) }}</td>
                <td class="py-2 pr-3">{{ key.createdAt | date: 'dd.MM.yyyy HH:mm' }}</td>
                <td class="py-2 pr-3">
                  {{ key.lastUsedAt ? (key.lastUsedAt | date: 'dd.MM.yyyy HH:mm') : 'nie' }}
                </td>
                <td class="py-2 pr-3">
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
                        <span class="ml-1 text-muted"
                          >bis {{ key.expiresAt | date: 'dd.MM.yyyy HH:mm' }}</span
                        >
                      }
                    }
                  }
                </td>
                <td class="py-2 text-right">
                  @if (statusOf(key) === 'active') {
                    <button
                      type="button"
                      class="rounded-md border border-danger px-2 py-1 text-danger hover:bg-surface-hover"
                      data-testid="revoke-key"
                      (click)="revoking.set(key)"
                    >
                      Widerrufen
                    </button>
                  }
                </td>
              </tr>
            } @empty {
              <tr>
                <td colspan="7" class="py-3 text-muted">Noch keine Keys.</td>
              </tr>
            }
          </tbody>
        </table>
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
