import { STATUS_DEFINITION, propertyDefinition, recordDto } from '../../core/testing/test-utils';
import { TITLE_KEY, canEditCell, withValue } from './inline-edit';

describe('canEditCell', () => {
  const text = propertyDefinition({ key: 'notes', type: 'text' });
  const computedType = propertyDefinition({ key: 'created', type: 'created_at' });
  const readOnly = propertyDefinition({ key: 'calc', type: 'number', readOnly: true });

  it.each([
    ['writer, text', text, null, true, true],
    ['writer, title', null, null, true, true],
    ['writer, status', STATUS_DEFINITION, null, true, true],
    ['viewer', text, null, false, false],
    ['archived record', text, '2026-11-07T00:00:00Z', true, false],
    ['computed type', computedType, null, true, false],
    ['readOnly property', readOnly, null, true, false],
  ] as const)('canEditCell_%s', (_, definition, archivedAt, canWrite, expected) => {
    expect(canEditCell(definition, { archivedAt }, canWrite)).toBe(expected);
  });

  it('withValue_UndefinedRemovesKey', () => {
    const record = recordDto({ properties: { status: 'todo', x: 1 } as never });

    expect(withValue(record, 'x', undefined).properties).toEqual({ status: 'todo' });
    expect(withValue(record, 'x', null).properties).toEqual({ status: 'todo', x: null });
    expect(withValue(record, TITLE_KEY, 'Neu').title).toBe('Neu');
  });
});
