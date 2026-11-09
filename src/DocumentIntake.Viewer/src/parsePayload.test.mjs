import assert from 'node:assert/strict';
import { after, test } from 'node:test';
import { createServer } from 'vite';

const server = await createServer({
  configFile: false,
  server: { middlewareMode: true },
  appType: 'custom',
});
after(() => server.close());
const { parsePayload, PayloadError } = await server.ssrLoadModule('/src/parsePayload.ts');

const field = (valueString) => ({
  type: 'string', valueString, confidence: 0.95, source: 'D(2,1,2,3,4)',
});

test('JSON loading includes unmapped fields and preserves types and locations', () => {
  const fields = Object.fromEntries(
    ['HippNumber', 'PolicyholderLastName', 'Unmapped1', 'Unmapped2', 'Unmapped3']
      .map((name) => [name, field(name)]),
  );
  fields.Boolean = { type: 'boolean', valueBoolean: false };
  fields.Number = { type: 'integer', valueInteger: 0 };
  fields.Date = { type: 'date', valueDate: '2000-01-02' };
  fields.Empty = { type: 'string' };
  for (const wrapper of [
    { payload: { contents: [{ fields }] } },
    { result: { contents: [{ fields }] } },
    { contents: [{ fields }] },
  ]) {
    const form = parsePayload(JSON.stringify(wrapper), { completedBlobUrl: 'https://example.com/doc.pdf' });
    assert.equal(form.fields.length, 9);
    assert.equal(form.fields.find((entry) => entry.column === 'Boolean').value, 'false');
    assert.equal(form.fields.find((entry) => entry.column === 'Number').value, '0');
    assert.equal(form.fields.find((entry) => entry.column === 'Date').type, 'date');
    assert.equal(form.fields.find((entry) => entry.column === 'Empty').value, null);
    assert.deepEqual(form.fields[0].boundingBox, { page: 2, polygon: [1, 2, 3, 4] });
    assert.equal(form.fields[0].confidence, 0.95);
    assert.equal(form.completedBlobUrl, 'https://example.com/doc.pdf');
  }
});

test('mapped JSON with analyzePayload displays the full embedded extraction', () => {
  const extractedFields = {
    ApplicationId: field('SYN-2026-0001'),
    CorrespondenceType: field('New HIPP application'),
    HippNumber: { type: 'integer', valueInteger: 70001 },
    PolicyholderLastName: field('Sample01'),
    PolicyholderDateOfBirth: { type: 'date', valueDate: '1976-02-04' },
    PolicyholderGetsMedicaid: { type: 'boolean', valueBoolean: true },
    PolicyholderState: { type: 'string', confidence: 0.836 },
  };
  const record = {
    formType: 'hipp-application',
    sourceBlobName: 'sample.pdf',
    completedBlobUrl: null,
    processedUtc: '2026-11-05T20:00:27.2068156+00:00',
    classificationConfidence: 1,
    analyzePayload: JSON.stringify({ contents: [{ fields: extractedFields }] }),
    fields: [1, 2, 3, 4].map((index) => ({ column: `cr417_field${index}`, value: 'mapped' })),
  };
  const form = parsePayload(JSON.stringify(record), { completedBlobUrl: 'https://example.com/doc.pdf' });
  assert.deepEqual(form.fields.map((entry) => entry.column), Object.keys(extractedFields));
  assert.equal(form.fields.find((entry) => entry.column === 'ApplicationId').value, 'SYN-2026-0001');
  assert.equal(form.fields.find((entry) => entry.column === 'PolicyholderState').value, null);
  assert.equal(form.formType, record.formType);
  assert.equal(form.sourceBlobName, record.sourceBlobName);
  assert.equal(form.processedUtc, record.processedUtc);
  assert.equal(form.classificationConfidence, 1);
  assert.equal(form.completedBlobUrl, 'https://example.com/doc.pdf');
  assert.deepEqual(parsePayload(JSON.stringify(form)), form);
  assert.equal(parsePayload(JSON.stringify({ ...record, analyzePayload: '' })).fields.length, 4);
  assert.throws(
    () => parsePayload(JSON.stringify({ ...record, analyzePayload: '{invalid' })),
    PayloadError,
  );
});

test('all content items and nested object/array leaves have distinct field keys', () => {
  const contents = [
    { fields: { Name: field('first'), Household: { type: 'array', valueArray: [
      { type: 'object', valueObject: { Name: field('member one') } },
      { type: 'object', valueObject: { Name: field('member two') } },
    ] } } },
    { fields: { Name: field('second') } },
  ];
  const form = parsePayload(JSON.stringify({ payload: { contents } }));
  assert.deepEqual(form.fields.map(({ column, value }) => [column, value]), [
    ['contents[0].Name', 'first'],
    ['contents[0].Household[0].Name', 'member one'],
    ['contents[0].Household[1].Name', 'member two'],
    ['contents[1].Name', 'second'],
  ]);
});

test('Dataverse loading uses mapped columns and ignores extracted JSON', () => {
  const record = {
    cr417_primarycolumn: 'https://example.com/saved.pdf',
    cr417_extractedjson: JSON.stringify([{ fields: { Unmapped: field('not shown') } }]),
  };
  for (let index = 1; index <= 4; index++) {
    record[`cr417_field${index}`] = 'saved';
    record[`cr417_field${index}confidence`] = 0.9;
    record[`cr417_field${index}source`] = 'D(1,1,2,3,4)';
  }
  const form = parsePayload(JSON.stringify(record));
  assert.deepEqual(form.fields.map((entry) => entry.column), [
    'cr417_field1', 'cr417_field2', 'cr417_field3', 'cr417_field4',
  ]);
  assert.equal(form.completedBlobUrl, record.cr417_primarycolumn);
});

test('mapped forms still round-trip and malformed content fails clearly', () => {
  const form = parsePayload(JSON.stringify({ fields: [{ column: 'extra', value: 'kept' }] }));
  assert.equal(parsePayload(JSON.stringify(form)).fields[0].value, 'kept');
  for (const contents of [[], [null], [{}]]) {
    assert.throws(() => parsePayload(JSON.stringify({ payload: { contents } })), PayloadError);
  }
});