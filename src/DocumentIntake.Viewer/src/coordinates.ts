import type { CoordinateUnits, FieldValue, UnitMode } from './types';

export interface Rect {
  left: number;
  top: number;
  width: number;
  height: number;
}

/**
 * Content Understanding emits inches for PDFs but normalized 0-1 for some sources.
 * Any coordinate above 1 can only be inches.
 */
export function detectUnits(fields: readonly FieldValue[]): CoordinateUnits {
  for (const field of fields) {
    for (const coordinate of field.boundingBox?.polygon ?? []) {
      if (coordinate > 1) {
        return 'inches';
      }
    }
  }

  return 'normalized';
}

export function resolveUnits(mode: UnitMode, fields: readonly FieldValue[]): CoordinateUnits {
  return mode === 'auto' ? detectUnits(fields) : mode;
}

/**
 * Converts a polygon to a CSS rect in viewport pixels. Content Understanding uses a top-left
 * origin, so this cannot go through viewport.convertToViewportPoint (PDF user space is bottom-left).
 */
export function polygonToRect(
  polygon: readonly number[],
  units: CoordinateUnits,
  viewport: { width: number; height: number; scale: number },
): Rect | null {
  if (polygon.length < 4 || polygon.length % 2 !== 0) {
    return null;
  }

  const scaleX = units === 'inches' ? 72 * viewport.scale : viewport.width;
  const scaleY = units === 'inches' ? 72 * viewport.scale : viewport.height;

  let minX = Infinity;
  let minY = Infinity;
  let maxX = -Infinity;
  let maxY = -Infinity;

  for (let i = 0; i < polygon.length; i += 2) {
    const x = polygon[i]! * scaleX;
    const y = polygon[i + 1]! * scaleY;
    minX = Math.min(minX, x);
    minY = Math.min(minY, y);
    maxX = Math.max(maxX, x);
    maxY = Math.max(maxY, y);
  }

  return { left: minX, top: minY, width: maxX - minX, height: maxY - minY };
}
