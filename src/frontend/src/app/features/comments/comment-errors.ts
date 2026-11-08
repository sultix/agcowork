import { ApiError, ProblemFieldError } from '../../core/api/api-error';
import { describeError } from '../../shared/ui/api-error-text';

/** What was attempted; picks the wording of `not_author` and `not_found`. */
export type CommentAction = 'add' | 'reply' | 'edit' | 'delete' | 'resolve';

const UUID_IN_TEXT = /[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}/i;

/** Texts for the 409 codes of comment writes. */
const CONFLICTS: Record<string, string> = {
  comment_deleted:
    'Der Kommentar wurde inzwischen gelöscht. Er kann weder bearbeitet noch beantwortet werden; starte einen neuen Thread.',
  record_archived:
    'Der Record ist archiviert, seine Kommentare sind schreibgeschützt. Stelle ihn zuerst wieder her.',
  collection_archived:
    'Die Collection dieses Records ist archiviert, Kommentare sind schreibgeschützt.',
  space_archived: 'Der Space dieses Records ist archiviert, Kommentare sind schreibgeschützt.',
  workspace_archived: 'Der Workspace ist archiviert, Kommentare sind schreibgeschützt.',
  idempotency_key_reused: 'Die Anfrage ließ sich nicht zuordnen. Bitte erneut senden.',
};

/**
 * German messages for a failed comment action, shown at its form: mentions of non-members (with
 * the name from the draft), wrong authors, replies to replies, deleted comments, archived
 * records. `markdown` is the text that was sent (to name the mentioned actor).
 */
export function commentErrorMessages(
  error: unknown,
  action: CommentAction,
  markdown = '',
): string[] {
  const apiError = ApiError.from(error);
  if (apiError.status === 400 && apiError.errors.length > 0) {
    return apiError.errors.map((e) => fieldMessage(e, markdown));
  }
  if (apiError.code === 'not_author') {
    return [
      action === 'delete'
        ? 'Löschen dürfen nur die Verfasserin oder der Verfasser und Mitglieder mit Admin-Recht.'
        : 'Nur die Verfasserin oder der Verfasser kann diesen Kommentar bearbeiten.',
    ];
  }
  if (apiError.code === 'not_found') {
    return [
      action === 'add'
        ? 'Der Record existiert nicht mehr.'
        : 'Der Kommentar existiert nicht mehr. Möglicherweise wurde er inzwischen gelöscht.',
    ];
  }
  const conflict = CONFLICTS[apiError.code];
  return [conflict ?? describeError(apiError)];
}

function fieldMessage(error: ProblemFieldError, markdown: string): string {
  switch (error.code) {
    case 'not_member': {
      const id = UUID_IN_TEXT.exec(error.message)?.[0];
      const name = id ? nameInDraft(markdown, id) : null;
      return name
        ? `${name} ist kein Mitglied dieses Workspaces und kann nicht erwähnt werden.`
        : 'Erwähnt werden können nur Mitglieder dieses Workspaces.';
    }
    case 'invalid_value':
      return error.field === 'markdown'
        ? 'Eine Erwähnung verweist auf keinen gültigen Actor. Wähle Mitglieder über „@“ aus.'
        : error.message;
    case 'not_root':
      return 'Nur der erste Kommentar eines Threads kann beantwortet oder erledigt werden.';
    case 'too_long':
      return 'Der Kommentar ist zu lang (höchstens 64 KiB). Lange Inhalte gehören in den Record.';
    case 'required':
      return 'Bitte einen Text eingeben.';
    case 'not_found':
      return 'Der Thread existiert nicht mehr.';
    default:
      return error.message;
  }
}

/** The link text of a mention of `actorId` in `markdown` (`@Name`), if there is one. */
function nameInDraft(markdown: string, actorId: string): string | null {
  const escaped = actorId.replace(/[-]/g, '\\-');
  const match = new RegExp(`\\[([^\\[\\]]*)\\]\\(\\s*<?actor:${escaped}>?\\s*\\)`, 'i').exec(
    markdown,
  );
  if (!match) {
    return null;
  }
  const label = match[1].trim();
  return label.startsWith('@') ? label : `@${label}`;
}
