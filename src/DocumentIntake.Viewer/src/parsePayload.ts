import { typeForColumn } from './labels';
import type { BoundingBox, FieldValue, MappedForm } from './types';

/**
 * Parses a Content Understanding source string, e.g. "D(1,0.5,1.0,2.5,1.0,2.5,1.4,0.5,1.4)".
 * Mirrors FieldMapper.ParseSource in DocumentIntake.Functions.
 */
export function parseSource(source: string | null | undefined): BoundingBox | null {
  if (!source) {
    return null;
  }

  const open = source.indexOf('(');
  const close = source.lastIndexOf(')');
  if (open < 0 || close <= open) {
    return null;
  }

  const parts = source
    .slice(open + 1, close)
    .split(',')
    .map((part) => part.trim())
    .filter((part) => part.length > 0);

  const [pageText, ...coordinateText] = parts;
  const page = Number.parseInt(pageText ?? '', 10);
  if (!Number.isFinite(page)) {
    return null;
  }

  const polygon: number[] = [];
  for (const text of coordinateText) {
    const coordinate = Number.parseFloat(text);
    if (Number.isFinite(coordinate)) {
      polygon.push(coordinate);
    }
  }

  return isUsablePolygon(polygon) ? { page, polygon } : null;
}

/** Needs at least two points, and x/y must pair up. */
export function isUsablePolygon(polygon: readonly number[] | null | undefined): boolean {
  return !!polygon && polygon.length >= 4 && polygon.length % 2 === 0;
}

export class PayloadError extends Error {}

interface ParseOptions {
  completedBlobUrl?: string;
}

export function parsePayload(text: string, options?: ParseOptions): MappedForm {
  let raw: unknown;
  try {
    raw = JSON.parse(text);
  } catch (error) {
    throw new PayloadError(`Not valid JSON: ${(error as Error).message}`);
  }

  if (typeof raw !== 'object' || raw === null || Array.isArray(raw)) {
    throw new PayloadError('Expected a JSON object.');
  }

  const record = raw as Record<string, unknown>;
  let form: MappedForm;
  if (typeof record.analyzePayload === 'string' && record.analyzePayload.trim()) {
    const extracted = parsePayload(record.analyzePayload, options);
    form = {
      ...extracted,
      formType: String(record.formType ?? extracted.formType),
      sourceBlobName: String(record.sourceBlobName ?? extracted.sourceBlobName),
      completedBlobUrl: record.completedBlobUrl == null
        ? extracted.completedBlobUrl
        : String(record.completedBlobUrl),
      processedUtc: String(record.processedUtc ?? extracted.processedUtc),
      classificationConfidence: Number(record.classificationConfidence ?? extracted.classificationConfidence),
    };
  } else if (Array.isArray(record.fields)) {
    form = readMappedForm(record);
  } else if (contentUnderstandingContents(record) !== null) {
    form = readContentUnderstandingPayload(contentUnderstandingContents(record)!, options?.completedBlobUrl ?? null);
  } else {
    form = readFlatRecord(record);
  }

  return options?.completedBlobUrl != null
    ? { ...form, completedBlobUrl: options.completedBlobUrl }
    : form;
}

function contentUnderstandingContents(record: Record<string, unknown>): unknown[] | null {
  for (const candidate of [record.payload, record.result, record]) {
    if (typeof candidate === 'object' && candidate !== null && !Array.isArray(candidate)) {
      const contents = (candidate as Record<string, unknown>).contents;
      if (Array.isArray(contents)) {
        return contents;
      }
    }
  }

  return null;
}

function readContentUnderstandingPayload(
  contents: unknown[],
  completedBlobUrl: string | null,
): MappedForm {
  if (contents.length === 0) {
    throw new PayloadError('Content Understanding payload contains no content items.');
  }

  const fields: FieldValue[] = [];
  function appendField(column: string, value: unknown): void {
    const field = typeof value === 'object' && value !== null && !Array.isArray(value)
      ? value as Record<string, unknown>
      : null;

    if (field?.valueObject && typeof field.valueObject === 'object' && !Array.isArray(field.valueObject)) {
      for (const [name, nested] of Object.entries(field.valueObject)) {
        appendField(`${column}.${name}`, nested);
      }
      return;
    }

    if (Array.isArray(field?.valueArray)) {
      field.valueArray.forEach((nested, index) => appendField(`${column}[${index}]`, nested));
      return;
    }

    const type = String(field?.type ?? 'string');
    const fieldValue = field ? extractContentUnderstandingValue(field, type) : value;

    fields.push({
      column,
      value: fieldValue == null ? null : String(fieldValue),
      confidence: Number(field?.confidence ?? 0),
      boundingBox: parseSource(field?.source as string | null | undefined),
      type,
    });
  }

  contents.forEach((content, index) => {
    if (typeof content !== 'object' || content === null || Array.isArray(content)) {
      throw new PayloadError(`Content Understanding contents[${index}] is not an object.`);
    }

    const fieldsRecord = (content as Record<string, unknown>).fields;
    if (typeof fieldsRecord !== 'object' || fieldsRecord === null || Array.isArray(fieldsRecord)) {
      throw new PayloadError(`Content Understanding contents[${index}].fields is missing.`);
    }

    for (const [name, value] of Object.entries(fieldsRecord)) {
      appendField(contents.length > 1 ? `contents[${index}].${name}` : name, value);
    }
  });

  return {
    formType: 'content-understanding',
    sourceBlobName: blobNameFromUrl(completedBlobUrl),
    completedBlobUrl,
    processedUtc: '',
    classificationConfidence: 0,
    fields,
  };
}

function extractContentUnderstandingValue(
  field: Record<string, unknown>,
  type: string,
): string | number | boolean | null {
  switch (type) {
    case 'boolean':
      return (field.valueBoolean as boolean | undefined) ?? null;
    case 'integer':
      return (field.valueInteger as number | undefined) ?? null;
    case 'number':
      return (field.valueNumber as number | undefined) ?? null;
    case 'date':
      return (field.valueDate as string | undefined) ?? null;
    default:
      break;
  }

  const stringValue = field.valueString;
  if (stringValue != null) {
    return String(stringValue);
  }

  for (const [key, value] of Object.entries(field)) {
    if (key.startsWith('value') && value != null) {
      if (typeof value === 'string' || typeof value === 'number' || typeof value === 'boolean') {
        return value;
      }
    }
  }

  return null;
}

function blobNameFromUrl(url: string | null): string {
  if (!url) {
    return '';
  }

  try {
    const parsed = new URL(url);
    const path = parsed.pathname;
    const slash = path.lastIndexOf('/');
    return slash >= 0 ? decodeURIComponent(path.slice(slash + 1)) : decodeURIComponent(path);
  } catch {
    return url;
  }
}

function readMappedForm(record: Record<string, unknown>): MappedForm {
  const fields = (record.fields as unknown[]).map((entry, index) => {
    if (typeof entry !== 'object' || entry === null) {
      throw new PayloadError(`fields[${index}] is not an object.`);
    }

    const field = entry as Record<string, unknown>;
    const box = field.boundingBox as Record<string, unknown> | null | undefined;
    const polygon = Array.isArray(box?.polygon) ? (box.polygon as unknown[]).map(Number) : null;

    return {
      column: String(field.column ?? `field${index}`),
      value: field.value == null ? null : String(field.value),
      confidence: Number(field.confidence ?? 0),
      boundingBox:
        box && isUsablePolygon(polygon) ? { page: Number(box.page ?? 1), polygon: polygon! } : null,
      type: String(field.type ?? 'string'),
    } satisfies FieldValue;
  });

  return {
    formType: String(record.formType ?? ''),
    sourceBlobName: String(record.sourceBlobName ?? ''),
    completedBlobUrl: record.completedBlobUrl == null ? null : String(record.completedBlobUrl),
    processedUtc: String(record.processedUtc ?? ''),
    classificationConfidence: Number(record.classificationConfidence ?? 0),
    fields,
  };
}

/** Handles the flattened shape produced by DataverseClient.BuildRecord. */
function readFlatRecord(record: Record<string, unknown>): MappedForm {
  const keys = Object.keys(record);
  const columns = keys.filter(
    (key) => keys.includes(`${key}confidence`) || keys.includes(`${key}source`),
  );

  if (columns.length === 0) {
    throw new PayloadError(
      'Unrecognised payload. Expected a MappedForm object with a "fields" array, or a flat Dataverse record with "<column>confidence" / "<column>source" keys.',
    );
  }

  const fields = columns.map((column) => {
    const value = record[column];
    return {
      column,
      value: value == null ? null : String(value),
      confidence: Number(record[`${column}confidence`] ?? 0),
      boundingBox: parseSource(record[`${column}source`] as string | null | undefined),
      type: typeForColumn(column),
    } satisfies FieldValue;
  });

  return {
    formType: '',
    sourceBlobName: '',
    completedBlobUrl: record.cr417_primarycolumn == null ? null : String(record.cr417_primarycolumn),
    processedUtc: '',
    classificationConfidence: 0,
    fields,
  };
}
