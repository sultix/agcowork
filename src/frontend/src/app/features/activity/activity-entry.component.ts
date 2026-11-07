import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, computed, inject, input, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { NgIcon, provideIcons } from '@ng-icons/core';
import { lucideChevronDown, lucideChevronUp } from '@ng-icons/lucide';
import { HlmButton } from '@spartan-ng/helm/button';
import { HlmTableImports } from '@spartan-ng/helm/table';
import type { components } from '../../core/api/generated/schema';
import { MembersStore } from '../../core/workspace/members.store';
import { WorkspaceContext } from '../../core/workspace/workspace-context.service';
import { ActorLabelComponent } from '../../shared/ui/actor-label.component';
import { formatValue } from '../records/history/diff';
import { WorkspaceTreeStore } from '../spaces/workspace-tree.store';
import { hasDetails, viewDiff } from './activity-diff';

export type ActivityEvent = components['schemas']['ActivityEventDto'];

/** Event type → what the target is and what was done to it. */
const EVENTS: Record<string, { article: string; noun: string; verb: string }> = {
  'record.created': { article: 'einen', noun: 'Record', verb: 'angelegt' },
  'record.updated': { article: 'einen', noun: 'Record', verb: 'geändert' },
  'record.appended': { article: 'einen', noun: 'Record', verb: 'ergänzt' },
  'record.archived': { article: 'einen', noun: 'Record', verb: 'archiviert' },
  'record.restored': { article: 'einen', noun: 'Record', verb: 'wiederhergestellt' },
  'space.created': { article: 'einen', noun: 'Space', verb: 'angelegt' },
  'space.updated': { article: 'einen', noun: 'Space', verb: 'geändert' },
  'space.archived': { article: 'einen', noun: 'Space', verb: 'archiviert' },
  'collection.created': { article: 'eine', noun: 'Collection', verb: 'angelegt' },
  'collection.updated': { article: 'eine', noun: 'Collection', verb: 'geändert' },
  'collection.schema_changed': { article: 'eine', noun: 'Collection', verb: 'im Schema geändert' },
  'collection.archived': { article: 'eine', noun: 'Collection', verb: 'archiviert' },
  'view.created': { article: 'eine', noun: 'Ansicht', verb: 'angelegt' },
  'view.updated': { article: 'eine', noun: 'Ansicht', verb: 'geändert' },
  'view.deleted': { article: 'eine', noun: 'Ansicht', verb: 'gelöscht' },
  'member.added': { article: 'ein', noun: 'Mitglied', verb: 'hinzugefügt' },
  'member.updated': { article: 'ein', noun: 'Mitglied', verb: 'geändert' },
  'member.removed': { article: 'ein', noun: 'Mitglied', verb: 'entfernt' },
};

/** Readable type texts, e.g. for the type filter. */
export const ACTIVITY_TYPE_LABELS: Readonly<Record<string, string>> = Object.fromEntries(
  Object.entries(EVENTS).map(([type, e]) => [type, `${e.noun} ${e.verb}`]),
);

/**
 * One entry of the activity feed: who (human or agent), what, on which target (link), when;
 * the diff opens on demand.
 */
@Component({
  selector: 'ag-activity-entry',
  imports: [DatePipe, RouterLink, NgIcon, HlmButton, HlmTableImports, ActorLabelComponent],
  providers: [provideIcons({ lucideChevronDown, lucideChevronUp })],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'block px-4 py-2 text-sm', 'data-testid': 'activity-item' },
  template: `
    <div class="flex flex-wrap items-baseline justify-between gap-x-4 gap-y-1">
      <span class="flex flex-wrap items-baseline gap-x-1.5">
        <ag-actor-label [actorId]="event().actorId" />
        <span data-testid="entry-text">{{ text() }}</span>
        @if (name(); as name) {
          <span class="text-muted-foreground" data-testid="entry-name">„{{ name }}“</span>
        }
        @if (link(); as link) {
          <a
            [routerLink]="link"
            class="font-medium underline underline-offset-4"
            data-testid="activity-link"
            >Öffnen</a
          >
        }
        @if (diff().affectedRecords !== null) {
          <span class="text-muted-foreground" data-testid="entry-affected"
            >({{ diff().affectedRecords }} Records bereinigt)</span
          >
        }
      </span>
      <span class="flex shrink-0 items-center gap-3 text-muted-foreground">
        @if (expandable()) {
          <button
            hlmBtn
            type="button"
            variant="ghost"
            size="xs"
            data-testid="entry-toggle"
            [attr.aria-expanded]="open()"
            (click)="open.set(!open())"
          >
            <ng-icon [name]="open() ? 'lucideChevronUp' : 'lucideChevronDown'" />
            {{ open() ? 'Details ausblenden' : 'Details' }}
          </button>
        }
        <span>{{ event().at | date: 'dd.MM.yyyy HH:mm' }}</span>
      </span>
    </div>
    @if (open() && expandable()) {
      <div class="mt-2 rounded-lg border bg-muted/30 p-3" data-testid="entry-diff">
        @if (diff().changes.length > 0) {
          <table hlmTable class="text-xs">
            <thead hlmTHead>
              <tr hlmTr>
                <th hlmTh class="h-8 w-1/4 px-2">Feld</th>
                <th hlmTh class="h-8 w-3/8 px-2">Alt</th>
                <th hlmTh class="h-8 w-3/8 px-2">Neu</th>
              </tr>
            </thead>
            <tbody hlmTBody>
              @for (change of diff().changes; track change.key) {
                <tr hlmTr data-testid="entry-change">
                  <td hlmTd class="p-2 font-mono">{{ change.key }}</td>
                  <td hlmTd class="p-2 break-all text-destructive" data-testid="change-old">
                    {{ format(change.old) }}
                  </td>
                  <td hlmTd class="p-2 break-all text-success" data-testid="change-new">
                    {{ format(change.new) }}
                  </td>
                </tr>
              }
            </tbody>
          </table>
        }
        @if (diff().body; as body) {
          <p class="mt-1 text-xs" data-testid="entry-body">
            Body geändert:
            <span class="text-success">+{{ body.added }}</span>
            <span class="text-destructive"> −{{ body.removed }}</span> Zeichen
          </p>
        }
        @for (extra of diff().extras; track extra.key) {
          <p class="mt-1 text-xs" data-testid="entry-extra">
            <span class="font-mono">{{ extra.key }}</span
            >: {{ extra.text }}
          </p>
        }
      </div>
    }
  `,
})
export class ActivityEntryComponent {
  private readonly members = inject(MembersStore);
  private readonly tree = inject(WorkspaceTreeStore);
  private readonly context = inject(WorkspaceContext);

  readonly event = input.required<ActivityEvent>();

  protected readonly open = signal(false);
  protected readonly diff = computed(() => viewDiff(this.event().diff));
  protected readonly expandable = computed(() => hasDetails(this.diff()));

  protected readonly text = computed(() => {
    const info = EVENTS[this.event().type];
    if (!info) {
      return this.event().type;
    }
    // With a name the noun is followed by it („Record „X“ geändert“); without, it is "einen Record".
    return this.name()
      ? `hat ${info.noun} ${info.verb}`
      : `hat ${info.article} ${info.noun} ${info.verb}`;
  });

  /** The target's name: from the diff (new title or name) or the navigation tree. */
  protected readonly name = computed(() => {
    const event = this.event();
    switch (event.targetType) {
      case 'record': {
        const title = (event.diff as { title?: { new?: unknown } } | null)?.title?.new;
        return typeof title === 'string' && title !== '' ? title : null;
      }
      case 'space':
        return this.tree.findSpace(event.targetId)?.name ?? null;
      case 'collection':
        return this.tree.findCollection(event.targetId)?.name ?? null;
      case 'member':
        return this.members.nameOf(event.targetId) || null;
      default:
        return null;
    }
  });

  protected readonly link = computed<string[] | null>(() => {
    const slug = this.context.slug();
    const event = this.event();
    if (!slug) {
      return null;
    }
    switch (event.targetType) {
      case 'record':
        return ['/w', slug, 'records', event.targetId];
      case 'space':
        return ['/w', slug, 'spaces', event.targetId];
      case 'collection':
        return ['/w', slug, 'collections', event.targetId];
      case 'view':
        return event.collectionId ? ['/w', slug, 'collections', event.collectionId] : null;
      case 'member':
        return ['/w', slug, 'settings', 'members'];
      default:
        return null;
    }
  });

  protected format(value: unknown): string {
    return formatValue(value);
  }
}
