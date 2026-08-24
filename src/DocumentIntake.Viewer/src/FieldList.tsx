import { useEffect, useRef } from 'react';
import { labelForColumn } from './labels';
import type { FieldValue } from './types';

interface FieldListProps {
  fields: FieldValue[];
  values: Record<string, string>;
  selectedIndex: number | null;
  onSelect: (index: number) => void;
  onChange: (column: string, value: string) => void;
}

function confidenceBand(confidence: number): string {
  if (confidence >= 0.9) {
    return 'high';
  }

  return confidence >= 0.7 ? 'medium' : 'low';
}

function inputTypeFor(type: string): string {
  if (type === 'date') {
    return 'date';
  }

  return type === 'integer' || type === 'number' ? 'number' : 'text';
}

/**
 * Converts a stored value into what the matching input control expects:
 * yyyy-MM-dd for dates, "true"/"false" for checkboxes, raw text otherwise.
 */
export function toInputValue(field: FieldValue): string {
  if (field.value === null) {
    return '';
  }

  if (field.type === 'date') {
    // Slicing the UTC ISO string avoids the local-timezone day shift.
    const parsed = new Date(field.value);
    return Number.isNaN(parsed.valueOf()) ? '' : parsed.toISOString().slice(0, 10);
  }

  if (field.type === 'boolean') {
    return String(field.value.toLowerCase() === 'true');
  }

  return field.value;
}

export function FieldList({ fields, values, selectedIndex, onSelect, onChange }: FieldListProps) {
  const listRef = useRef<HTMLUListElement>(null);

  useEffect(() => {
    if (selectedIndex === null) {
      return;
    }

    listRef.current?.children[selectedIndex]?.scrollIntoView({ block: 'nearest' });
  }, [selectedIndex]);

  return (
    <ul className="field-list" ref={listRef}>
      {fields.map((field, index) => {
        const inputId = `field-${field.column}`;
        const labelId = `${inputId}-label`;
        const value = values[field.column] ?? '';
        const isBoolean = field.type === 'boolean';
        // Focus selects too, so tabbing through the list walks the highlights.
        const shared = { id: inputId, className: 'field-input', onFocus: () => onSelect(index) };

        return (
          <li
            key={field.column}
            className={`field${index === selectedIndex ? ' selected' : ''}${field.boundingBox ? '' : ' unlocatable'}`}
            onClick={() => onSelect(index)}
          >
            {/*
              A label's clicks are forwarded to its control, which would toggle a checkbox
              just from selecting the row. Booleans get the name via aria-labelledby instead.
            */}
            {isBoolean ? (
              <span className="field-label" id={labelId}>
                {labelForColumn(field.column)}
              </span>
            ) : (
              <label className="field-label" htmlFor={inputId}>
                {labelForColumn(field.column)}
              </label>
            )}

            {isBoolean ? (
              <input
                {...shared}
                type="checkbox"
                aria-labelledby={labelId}
                checked={value === 'true'}
                onChange={(event) => onChange(field.column, String(event.target.checked))}
              />
            ) : (
              <input
                {...shared}
                type={inputTypeFor(field.type)}
                value={value}
                onChange={(event) => onChange(field.column, event.target.value)}
              />
            )}

            <span className={`confidence ${confidenceBand(field.confidence)}`}>
              {(field.confidence * 100).toFixed(0)}%
            </span>
            {!field.boundingBox && <span className="no-box">no location</span>}
          </li>
        );
      })}
    </ul>
  );
}
