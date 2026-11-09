import { propertyDefinition } from '../../../core/testing/test-utils';
import {
  EMPTY_SEED,
  TemplateDto,
  TemplateSeed,
  bodyExcerpt,
  cleanPreset,
  presetNames,
  presettable,
  samePreset,
  seedFromRecord,
  seedOf,
  toCreateBody,
  toUpdateBody,
  withPresetValue,
} from './template-draft';
import { mapTemplateErrors } from './template-errors';
import { ApiError } from '../../../core/api/api-error';

const SCHEMA = [
  propertyDefinition({ key: 'title', name: 'Titel', type: 'text' }),
  propertyDefinition({ key: 'status', name: 'Status', type: 'status' }),
  propertyDefinition({ key: 'estimate', name: 'Schätzung', type: 'number' }),
  propertyDefinition({ key: 'created', name: 'Angelegt', type: 'created_at' }),
  propertyDefinition({ key: 'locked', name: 'Gesperrt', type: 'text', readOnly: true }),
];

function template(overrides: Partial<TemplateDto> = {}): TemplateDto {
  return {
    id: 't1',
    collectionId: 'c1',
    name: 'ADR',
    description: 'Entscheidungen',
    propertiesPreset: { status: 'todo', estimate: 3 } as never,
    bodyMarkdown: '# Kontext',
    isDefault: false,
    createdBy: 'a1',
    createdAt: '2026-11-07T10:00:00Z',
    ...overrides,
  };
}

describe('template-draft', () => {
  it('presettable_Schema_IncludesTitleButNotComputedOrReadOnly', () => {
    expect(presettable(SCHEMA).map((p) => p.key)).toEqual(['title', 'status', 'estimate']);
  });

  it('seedFromRecord_Record_TakesPresettableValuesBodyAndTitleAsName', () => {
    const seed = seedFromRecord(SCHEMA, {
      title: '  Login bauen ',
      properties: { status: 'done', estimate: 5, created: 'x', locked: 'y', extra: 1 },
      body: 'Text',
    });

    expect(seed).toEqual({
      name: 'Login bauen',
      description: '',
      isDefault: false,
      preset: { status: 'done', estimate: 5 },
      body: 'Text',
    });
  });

  it('seedFromRecord_EmptyValues_AreLeftOut', () => {
    const seed = seedFromRecord(SCHEMA, {
      title: 'A',
      properties: { status: null, estimate: '' },
    });

    expect(seed.preset).toEqual({});
    expect(seed.body).toBe('');
  });

  it('withPresetValue_EmptyValue_RemovesTheEntry', () => {
    expect(withPresetValue({ a: 1, b: 2 }, 'a', null)).toEqual({ b: 2 });
    expect(withPresetValue({ a: 1 }, 'a', [])).toEqual({});
    expect(withPresetValue({ a: 1 }, 'b', false)).toEqual({ a: 1, b: false });
  });

  it('cleanPreset_KeepsFalseAndZero', () => {
    expect(cleanPreset({ a: false, b: 0, c: '', d: null })).toEqual({ a: false, b: 0 });
  });

  it('samePreset_KeyOrderDiffers_IsEqual', () => {
    expect(samePreset({ a: 1, b: { x: 1, y: 2 } }, { b: { y: 2, x: 1 }, a: 1 })).toBe(true);
    expect(samePreset({ a: 1 }, { a: 2 })).toBe(false);
  });

  it('toUpdateBody_NothingChanged_IsNull', () => {
    const seed = seedOf(template());

    expect(toUpdateBody(seed, { ...seed })).toBeNull();
  });

  it('toUpdateBody_OnlyNameChanged_SendsOnlyName', () => {
    const seed = seedOf(template());

    expect(toUpdateBody(seed, { ...seed, name: ' Neu ' })).toEqual({ name: 'Neu' });
  });

  it('toUpdateBody_PresetEntryRemoved_SendsCompletePresetWithoutIt', () => {
    const seed = seedOf(template());
    const rest = withPresetValue(seed.preset, 'status', null);

    expect(toUpdateBody(seed, { ...seed, preset: rest })).toEqual({
      propertiesPreset: { estimate: 3 },
    });
  });

  it('toUpdateBody_DescriptionAndBodyCleared_SendEmptyStrings', () => {
    const seed = seedOf(template());

    expect(toUpdateBody(seed, { ...seed, description: '', body: '' })).toEqual({
      description: '',
      bodyMarkdown: '',
    });
  });

  it('toUpdateBody_DefaultToggled_SendsIsDefault', () => {
    const seed = seedOf(template());

    expect(toUpdateBody(seed, { ...seed, isDefault: true })).toEqual({ isDefault: true });
  });

  it('toCreateBody_Draft_OmitsEmptyDescriptionAndBody', () => {
    const draft: TemplateSeed = { ...EMPTY_SEED, name: ' ADR ', preset: { a: 1, b: null } };

    expect(toCreateBody(draft)).toEqual({
      name: 'ADR',
      propertiesPreset: { a: 1 },
      isDefault: false,
    });
  });

  it('bodyExcerpt_Markdown_ReturnsPlainStartAndTruncates', () => {
    expect(bodyExcerpt('# Kontext\n\n- **Fett** und [Link](http://x)\n')).toBe(
      'Kontext Fett und Link',
    );
    expect(bodyExcerpt('a'.repeat(300), 10)).toBe('aaaaaaaaaa …');
  });

  it('presetNames_Template_ListsSchemaNamesThenUnknownKeys', () => {
    expect(
      presetNames(
        template({ propertiesPreset: { estimate: 1, gone: 2, status: 'x' } as never }),
        SCHEMA,
      ),
    ).toEqual(['Status', 'Schätzung', 'gone']);
  });
});

describe('mapTemplateErrors', () => {
  const error = (errors: { field: string; code: string; allowed?: string[] }[], status = 400) =>
    new ApiError(
      status,
      'validation_failed',
      errors.map((e) => ({ ...e, message: e.code })),
    );

  it('mapTemplateErrors_PresetField_BelongsToItsProperty', () => {
    const [mapped] = mapTemplateErrors(
      error([{ field: 'propertiesPreset.status', code: 'not_in_options', allowed: ['todo'] }]),
    );

    expect(mapped).toEqual({
      kind: 'preset',
      target: 'status',
      message: 'Wert ist nicht erlaubt. Erlaubt: todo.',
    });
  });

  it('mapTemplateErrors_NameAndBody_BelongToTheirFields', () => {
    const mapped = mapTemplateErrors(
      error([
        { field: 'name', code: 'required' },
        { field: 'bodyMarkdown', code: 'too_long' },
      ]),
    );

    expect(mapped.map((m) => [m.kind, m.target])).toEqual([
      ['field', 'name'],
      ['field', 'bodyMarkdown'],
    ]);
  });

  it('mapTemplateErrors_NoFieldErrors_BecomesFormMessage', () => {
    const mapped = mapTemplateErrors(new ApiError(409, 'collection_archived'));

    expect(mapped).toHaveLength(1);
    expect(mapped[0].kind).toBe('form');
    expect(mapped[0].message).toContain('archiviert');
  });
});
