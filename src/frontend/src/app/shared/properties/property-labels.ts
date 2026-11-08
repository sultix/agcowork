import type { PropertyType } from './property-types';

/** German display names of the property types (architecture §2 "Property-Typen"). */
export const PROPERTY_TYPE_LABELS: Readonly<Record<PropertyType, string>> = {
  text: 'Text',
  markdown: 'Markdown',
  number: 'Zahl',
  checkbox: 'Checkbox',
  select: 'Auswahl',
  multi_select: 'Mehrfachauswahl',
  status: 'Status',
  date: 'Datum',
  datetime: 'Datum und Uhrzeit',
  url: 'URL',
  email: 'E-Mail',
  relation: 'Relation',
  actor: 'Actor (Mensch oder Agent)',
  json: 'JSON',
  created_at: 'Erstellt am',
  updated_at: 'Geändert am',
  created_by: 'Erstellt von',
  updated_by: 'Geändert von',
};

/** Label of a type; unknown wire names (a newer server) are shown as they are. */
export function propertyTypeLabel(type: string): string {
  return PROPERTY_TYPE_LABELS[type as PropertyType] ?? type;
}

/** Types a user can pick for a new property, grouped as in a type picker. */
export const PROPERTY_TYPE_GROUPS: readonly {
  readonly label: string;
  readonly types: readonly PropertyType[];
}[] = [
  { label: 'Text', types: ['text', 'markdown', 'url', 'email'] },
  { label: 'Werte', types: ['number', 'checkbox', 'date', 'datetime'] },
  { label: 'Auswahl', types: ['select', 'multi_select', 'status'] },
  { label: 'Verweise', types: ['relation', 'actor'] },
  { label: 'Sonstige', types: ['json'] },
  {
    label: 'Berechnet (füllt das System)',
    types: ['created_at', 'updated_at', 'created_by', 'updated_by'],
  },
];

/** The option color palette of the API (`PropertyOption.Colors`), with a swatch class per color. */
export const OPTION_COLORS: readonly {
  readonly value: string;
  readonly label: string;
  readonly swatch: string;
}[] = [
  { value: 'gray', label: 'Grau', swatch: 'bg-gray-400' },
  { value: 'brown', label: 'Braun', swatch: 'bg-amber-800' },
  { value: 'orange', label: 'Orange', swatch: 'bg-orange-400' },
  { value: 'yellow', label: 'Gelb', swatch: 'bg-yellow-400' },
  { value: 'green', label: 'Grün', swatch: 'bg-green-500' },
  { value: 'blue', label: 'Blau', swatch: 'bg-blue-500' },
  { value: 'purple', label: 'Lila', swatch: 'bg-purple-500' },
  { value: 'pink', label: 'Pink', swatch: 'bg-pink-400' },
  { value: 'red', label: 'Rot', swatch: 'bg-red-500' },
];

/** Swatch class of an option color; `null` for no or an unknown color. */
export function optionSwatch(color: string | null | undefined): string | null {
  return OPTION_COLORS.find((c) => c.value === color)?.swatch ?? null;
}

/** Status groups of the API with their German names. */
export const STATUS_GROUPS: readonly { readonly value: string; readonly label: string }[] = [
  { value: 'todo', label: 'Offen' },
  { value: 'in_progress', label: 'In Arbeit' },
  { value: 'done', label: 'Erledigt' },
];
