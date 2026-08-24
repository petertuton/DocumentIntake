import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { FieldList, toInputValue } from './FieldList';
import { PayloadInput } from './PayloadInput';
import { PdfPage, type Highlight } from './PdfPage';
import { resolveUnits } from './coordinates';
import { sortFields } from './labels';
import { PayloadError, parsePayload } from './parsePayload';
import type { MappedForm, UnitMode } from './types';
import { usePdfDocument } from './usePdfDocument';

const StorageKey = 'document-intake-viewer.payload';
const ZoomLevels = [0.75, 1, 1.25, 1.5, 2];

/** Matches the horizontal padding on .pdf-pane. */
const PanePadding = 32;

function describeRecordFailure(status: number): string {
  switch (status) {
    case 400:
      return 'That record id is not a valid GUID.';
    case 401:
      return 'Unauthorized. Check FUNCTION_APP_KEY in .env.local and restart the dev server.';
    case 404:
      return 'No Dataverse record with that id.';
    case 502:
      return 'The API could not read from Dataverse. Check the Function App logs.';
    default:
      return `Record request failed with HTTP ${status}.`;
  }
}

export function App() {
  const [form, setForm] = useState<MappedForm | null>(null);
  const [values, setValues] = useState<Record<string, string>>({});
  const [payloadError, setPayloadError] = useState<string | null>(null);
  const [selectedIndex, setSelectedIndex] = useState<number | null>(null);
  const [unitMode, setUnitMode] = useState<UnitMode>('auto');
  const [zoom, setZoom] = useState<'fit' | number>('fit');
  const [paneWidth, setPaneWidth] = useState(0);
  const [pageWidth, setPageWidth] = useState(0);
  const [fetchingRecord, setFetchingRecord] = useState(false);
  const [saveNotice, setSaveNotice] = useState<{ id: number; text: string } | null>(null);
  const paneRef = useRef<HTMLElement>(null);

  const load = useCallback((text: string, remember: boolean) => {
    try {
      const parsed = parsePayload(text);
      const fields = sortFields(parsed.fields);
      setForm({ ...parsed, fields });
      setValues(Object.fromEntries(fields.map((field) => [field.column, toInputValue(field)])));
      setPayloadError(null);
      setSelectedIndex(null);
      if (remember) {
        localStorage.setItem(StorageKey, text);
      }
    } catch (error) {
      setPayloadError(error instanceof PayloadError ? error.message : String(error));
    }
  }, []);

  useEffect(() => {
    const saved = localStorage.getItem(StorageKey);
    if (saved) {
      load(saved, false);
    }
  }, [load]);

  const loadRecord = useCallback(
    async (recordId: string) => {
      setFetchingRecord(true);
      try {
        const response = await fetch(`/api/documents/record?id=${encodeURIComponent(recordId)}`);
        if (!response.ok) {
          setPayloadError(describeRecordFailure(response.status));
          return;
        }

        load(await response.text(), true);
      } catch (error) {
        setPayloadError(`Could not reach the API: ${(error as Error).message}`);
      } finally {
        setFetchingRecord(false);
      }
    },
    [load],
  );

  const loadBlobWithContentUnderstanding = useCallback(
    async (blobUrl: string, contentUnderstandingJson: string) => {
      if (!blobUrl) {
        setPayloadError('Enter a blob URL first.');
        return;
      }

      setFetchingRecord(true);
      try {
        const parsed = parsePayload(contentUnderstandingJson, { completedBlobUrl: blobUrl });
        const text = JSON.stringify(parsed);
        load(text, true);
      } catch (error) {
        setPayloadError(error instanceof PayloadError ? error.message : String(error));
      } finally {
        setFetchingRecord(false);
      }
    },
    [load],
  );

  const { document: pdf, loading, error: pdfError } = usePdfDocument(form?.completedBlobUrl ?? null);
  const units = useMemo(() => resolveUnits(unitMode, form?.fields ?? []), [unitMode, form]);

  useEffect(() => {
    const pane = paneRef.current;
    if (!pane) {
      return;
    }

    const observer = new ResizeObserver(([entry]) => setPaneWidth(entry?.contentRect.width ?? 0));
    observer.observe(pane);
    return () => observer.disconnect();
  }, [form]);

  useEffect(() => {
    if (!pdf) {
      setPageWidth(0);
      return;
    }

    let cancelled = false;
    void pdf.getPage(1).then((page) => {
      if (!cancelled) {
        setPageWidth(page.getViewport({ scale: 1 }).width);
      }
    });

    return () => {
      cancelled = true;
    };
  }, [pdf]);

  useEffect(() => {
    if (!saveNotice) {
      return;
    }

    const timer = setTimeout(() => setSaveNotice(null), 3000);
    return () => clearTimeout(timer);
  }, [saveNotice]);

  const scale = useMemo(() => {
    if (zoom !== 'fit') {
      return zoom;
    }

    // Hold at 1 until both measurements land, otherwise the first render is a guess.
    return pageWidth > 0 && paneWidth > 0 ? (paneWidth - PanePadding) / pageWidth : 1;
  }, [zoom, paneWidth, pageWidth]);

  const highlightsByPage = useMemo(() => {
    const byPage = new Map<number, Highlight[]>();
    form?.fields.forEach((field, index) => {
      if (!field.boundingBox) {
        return;
      }

      const page = byPage.get(field.boundingBox.page) ?? [];
      page.push({ index, polygon: field.boundingBox.polygon });
      byPage.set(field.boundingBox.page, page);
    });
    return byPage;
  }, [form]);

  if (!form) {
    return (
      <PayloadInput
        error={payloadError}
        busy={fetchingRecord}
        onLoadRecord={loadRecord}
        onLoadBlobWithContentUnderstanding={loadBlobWithContentUnderstanding}
      />
    );
  }

  return (
    <div className="app">
      <header>
        <div className="header-actions">
          <button
            type="button"
            className="primary"
            onClick={() => {
              localStorage.removeItem(StorageKey);
              setForm(null);
            }}
          >
            Fetch Document
          </button>
          {/* Placeholder until the Dataverse write path exists - this persists nothing. */}
          <button
            type="button"
            className="primary"
            onClick={() => setSaveNotice({ id: Date.now(), text: 'Changes saved (demo only)' })}
          >
            Save
          </button>
          {saveNotice && (
            <span className="save-notice" role="status">
              {saveNotice.text}
            </span>
          )}
        </div>
        <div className="meta">
          <span>{form.sourceBlobName}</span>
          {form.classificationConfidence > 0 && (
            <span>classification {(form.classificationConfidence * 100).toFixed(0)}%</span>
          )}
        </div>
        <div className="controls">
          <label>
            Units
            <select value={unitMode} onChange={(event) => setUnitMode(event.target.value as UnitMode)}>
              <option value="auto">Auto ({units})</option>
              <option value="inches">Inches</option>
              <option value="normalized">Normalized</option>
            </select>
          </label>
          <label>
            Zoom
            <select
              value={zoom === 'fit' ? 'fit' : String(zoom)}
              onChange={(event) =>
                setZoom(event.target.value === 'fit' ? 'fit' : Number(event.target.value))
              }
            >
              <option value="fit">Fit width</option>
              {ZoomLevels.map((level) => (
                <option key={level} value={level}>
                  {level * 100}%
                </option>
              ))}
            </select>
          </label>
        </div>
      </header>

      <main>
        <section className="fields-pane">
          <FieldList
            fields={form.fields}
            values={values}
            selectedIndex={selectedIndex}
            onSelect={setSelectedIndex}
            onChange={(column, value) => setValues((current) => ({ ...current, [column]: value }))}
          />
        </section>

        <section className="pdf-pane" ref={paneRef}>
          {!form.completedBlobUrl && <p className="notice">Payload has no completedBlobUrl, so there is no PDF to show.</p>}
          {loading && <p className="notice">Loading document…</p>}
          {pdfError && <p className="notice error">{pdfError}</p>}
          {pdf &&
            Array.from({ length: pdf.numPages }, (_, i) => i + 1).map((pageNumber) => (
              <PdfPage
                key={pageNumber}
                document={pdf}
                pageNumber={pageNumber}
                scale={scale}
                units={units}
                highlights={highlightsByPage.get(pageNumber) ?? []}
                selectedIndex={selectedIndex}
                onSelect={setSelectedIndex}
              />
            ))}
        </section>
      </main>
    </div>
  );
}
