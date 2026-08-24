import analyzer from '../../../analyzers/contentunderstanding/document-intake-form-analyzer.json';
import fieldMap from '../../../analyzers/contentunderstanding/field-map.json';
import type { FieldValue } from './types';

const columnToFieldName = new Map<string, string>(
  Object.entries(fieldMap.columns).map(([fieldName, column]) => [column, fieldName]),
);

const analyzerFields = analyzer.fieldSchema.fields as Record<string, { type?: string }>;

/** Display label and running order, keyed by Content Understanding field name. */
const display = new Map<string, { label: string; order: number }>([
  ['HippNumber', { label: 'HIPP Number', order: 1 }],
  ['PolicyholderLastName', { label: 'Last Name', order: 2 }],
  ['PolicyholderDateOfBirth', { label: 'Date of Birth', order: 3 }],
  ['PolicyholderGetsMedicaid', { label: 'Does the policy holder get Medicaid', order: 4 }],
]);

/** Splits a PascalCase Content Understanding field name into words. */
function humanise(fieldName: string): string {
  return fieldName.replace(/([a-z0-9])([A-Z])/g, '$1 $2');
}

export function labelForColumn(column: string): string {
  const fieldName = columnToFieldName.get(column);
  if (!fieldName) {
    return column;
  }

  return display.get(fieldName)?.label ?? humanise(fieldName);
}

/** Dataverse stores no type information, so recover it from the analyzer schema. */
export function typeForColumn(column: string): string {
  const fieldName = columnToFieldName.get(column);
  return (fieldName && analyzerFields[fieldName]?.type) || 'string';
}

/** Unlisted fields keep their incoming order, after the ones with an explicit position. */
export function sortFields(fields: readonly FieldValue[]): FieldValue[] {
  return fields
    .map((field, index) => ({
      field,
      order: display.get(columnToFieldName.get(field.column) ?? '')?.order ?? Number.MAX_SAFE_INTEGER,
      index,
    }))
    .sort((a, b) => a.order - b.order || a.index - b.index)
    .map((entry) => entry.field);
}
