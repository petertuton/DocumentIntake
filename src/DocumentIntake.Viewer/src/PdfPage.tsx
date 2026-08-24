import { useEffect, useRef, useState } from 'react';
import type { PDFDocumentProxy, PageViewport } from 'pdfjs-dist';
import { polygonToRect } from './coordinates';
import type { CoordinateUnits } from './types';

export interface Highlight {
  index: number;
  polygon: number[];
}

interface PdfPageProps {
  document: PDFDocumentProxy;
  pageNumber: number;
  scale: number;
  units: CoordinateUnits;
  highlights: Highlight[];
  selectedIndex: number | null;
  onSelect: (index: number) => void;
}

export function PdfPage({
  document,
  pageNumber,
  scale,
  units,
  highlights,
  selectedIndex,
  onSelect,
}: PdfPageProps) {
  const canvasRef = useRef<HTMLCanvasElement>(null);
  const selectedRef = useRef<HTMLButtonElement>(null);
  const [viewport, setViewport] = useState<PageViewport | null>(null);

  useEffect(() => {
    let cancelled = false;
    let task: { cancel: () => void } | null = null;

    (async () => {
      const page = await document.getPage(pageNumber);
      const pageViewport = page.getViewport({ scale });
      const canvas = canvasRef.current;
      if (cancelled || !canvas) {
        return;
      }

      // Render at device resolution but lay out in CSS pixels so overlay maths stays in viewport units.
      const ratio = window.devicePixelRatio || 1;
      canvas.width = Math.floor(pageViewport.width * ratio);
      canvas.height = Math.floor(pageViewport.height * ratio);
      canvas.style.width = `${pageViewport.width}px`;
      canvas.style.height = `${pageViewport.height}px`;

      const context = canvas.getContext('2d');
      if (!context) {
        return;
      }

      const renderTask = page.render({
        canvas,
        canvasContext: context,
        viewport: pageViewport,
        transform: ratio === 1 ? undefined : [ratio, 0, 0, ratio, 0, 0],
      });
      task = renderTask;

      try {
        await renderTask.promise;
      } catch {
        return;
      }

      if (!cancelled) {
        setViewport(pageViewport);
      }
    })();

    return () => {
      cancelled = true;
      task?.cancel();
    };
  }, [document, pageNumber, scale]);

  // Pages are taller than the pane, so track the box rather than the page.
  useEffect(() => {
    selectedRef.current?.scrollIntoView({ behavior: 'smooth', block: 'center' });
  }, [selectedIndex, viewport]);

  return (
    <div className="pdf-page" style={viewport ? { width: viewport.width, height: viewport.height } : undefined}>
      <canvas ref={canvasRef} />
      {viewport &&
        highlights.map((highlight) => {
          const rect = polygonToRect(highlight.polygon, units, viewport);
          if (!rect) {
            return null;
          }

          const selected = highlight.index === selectedIndex;

          return (
            <button
              key={highlight.index}
              ref={selected ? selectedRef : undefined}
              type="button"
              className={`highlight${selected ? ' selected' : ''}`}
              style={{ left: rect.left, top: rect.top, width: rect.width, height: rect.height }}
              onClick={() => onSelect(highlight.index)}
            />
          );
        })}
      <span className="page-number">{pageNumber}</span>
    </div>
  );
}
