import { useEffect, useState } from 'react';
import { getDocument, GlobalWorkerOptions, type PDFDocumentProxy } from 'pdfjs-dist';
import workerUrl from 'pdfjs-dist/build/pdf.worker.min.mjs?url';

GlobalWorkerOptions.workerSrc = workerUrl;

export interface PdfState {
  document: PDFDocumentProxy | null;
  loading: boolean;
  error: string | null;
}

function describeFailure(status: number): string {
  switch (status) {
    case 400:
      return 'The blob URL was rejected. It must be an https URL on the configured storage account, in the "completed" container, with no SAS token.';
    case 401:
      return 'Unauthorized. Check FUNCTION_APP_KEY in .env.local and restart the dev server.';
    case 404:
      return 'The blob no longer exists at that URL.';
    default:
      return `Document request failed with HTTP ${status}.`;
  }
}

export function usePdfDocument(blobUrl: string | null): PdfState {
  const [state, setState] = useState<PdfState>({ document: null, loading: false, error: null });

  useEffect(() => {
    if (!blobUrl) {
      setState({ document: null, loading: false, error: null });
      return;
    }

    let cancelled = false;
    let task: ReturnType<typeof getDocument> | null = null;
    const controller = new AbortController();
    setState({ document: null, loading: true, error: null });

    (async () => {
      try {
        const response = await fetch(`/api/documents/content?url=${encodeURIComponent(blobUrl)}`, {
          signal: controller.signal,
        });

        if (!response.ok) {
          throw new Error(describeFailure(response.status));
        }

        const bytes = await response.arrayBuffer();
        task = getDocument({ data: new Uint8Array(bytes) });
        const loaded = await task.promise;

        if (cancelled) {
          return;
        }

        setState({ document: loaded, loading: false, error: null });
      } catch (error) {
        if (cancelled || controller.signal.aborted) {
          return;
        }

        setState({ document: null, loading: false, error: (error as Error).message });
      }
    })();

    return () => {
      cancelled = true;
      controller.abort();
      void task?.destroy();
    };
  }, [blobUrl]);

  return state;
}
