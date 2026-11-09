import { schemaFieldMessage } from './schema-errors';

// Messages as the server sends them (CollectionSchema.cs, UpdateSchema.cs).
describe('schemaFieldMessage', () => {
  it('schemaFieldMessage_Duplicate_GermanTextOnly', () => {
    expect(
      schemaFieldMessage({
        field: 'add[1].key',
        code: 'duplicate',
        message: "Key 'status' occurs more than once. Keys must be unique in a collection.",
      }),
    ).toBe('Kommt bereits vor. Bitte einen anderen Wert wählen.');
  });

  it('schemaFieldMessage_TypeChangeNotAllowed_ShowsAllowedWithoutServerMessage', () => {
    expect(
      schemaFieldMessage({
        field: 'update[0].type',
        code: 'type_change_not_allowed',
        message: "A 'number' property cannot become 'text'.",
        allowed: ['number'],
      }),
    ).toBe(
      'Dieser Typwechsel ist nicht erlaubt (nur Text ↔ Markdown und Auswahl → Mehrfachauswahl). Erlaubt: number.',
    );
  });

  it('schemaFieldMessage_NotAllowed_AppendsServerReason', () => {
    expect(
      schemaFieldMessage({
        field: 'add[0].required',
        code: 'not_allowed',
        message: 'The title property is always required.',
      }),
    ).toBe('Nicht erlaubt. Hinweis: The title property is always required.');
  });

  it('schemaFieldMessage_CompleteCodes_KeepGermanTextOnly', () => {
    expect(
      schemaFieldMessage({
        field: 'add[0].key',
        code: 'reserved',
        message: 'Reserved: id.',
        allowed: ['id'],
      }),
    ).toBe(
      'Dieser Key ist reserviert (id, collection, space, revision, body und title). Bitte einen anderen wählen.',
    );
  });

  it('schemaFieldMessage_TooLong_UsesSharedTextWithLimit', () => {
    expect(
      schemaFieldMessage({
        field: 'add[0].name',
        code: 'too_long',
        message: "'add[0].name' must not exceed 100 characters.",
      }),
    ).toBe('Zu lang. Hinweis: Must not exceed 100 characters.');
  });

  it('schemaFieldMessage_UnknownCode_UsesServerMessage', () => {
    expect(schemaFieldMessage({ field: 'x', code: 'weird', message: 'Server says no.' })).toBe(
      'Server says no.',
    );
  });
});
