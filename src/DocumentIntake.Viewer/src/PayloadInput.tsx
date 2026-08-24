import { useState } from 'react';

interface PayloadInputProps {
  error: string | null;
  busy: boolean;
  onLoadRecord: (recordId: string) => void;
  onLoadBlobWithContentUnderstanding: (blobUrl: string, contentUnderstandingJson: string) => void;
}

export function PayloadInput({
  error,
  busy,
  onLoadRecord,
  onLoadBlobWithContentUnderstanding,
}: PayloadInputProps) {
  const [text, setText] = useState('');
  const [recordId, setRecordId] = useState('');
  const [blobUrl, setBlobUrl] = useState('');
  const [dragging, setDragging] = useState(false);

  async function handleDrop(event: React.DragEvent) {
    event.preventDefault();
    setDragging(false);

    const file = event.dataTransfer.files[0];
    if (file) {
      const contents = await file.text();
      setText(contents);
    }
  }

  const hasRecordId = recordId.trim().length > 0;
  const hasBlobUrl = blobUrl.trim().length > 0;
  const hasJson = text.trim().length > 0;

  const canLoad = hasRecordId || (hasBlobUrl && hasJson);

  function handleLoad() {
    if (hasRecordId) {
      onLoadRecord(recordId.trim());
      return;
    }

    onLoadBlobWithContentUnderstanding(blobUrl.trim(), text);
  }

  return (
    <div
      className={`payload-input${dragging ? ' dragging' : ''}`}
      onDragOver={(event) => {
        event.preventDefault();
        setDragging(true);
      }}
      onDragLeave={() => setDragging(false)}
      onDrop={handleDrop}
    >
      <h1>Document Intake Viewer</h1>
      <p className="or">Provide either a Dataverse record id, OR provide both Blob URL and Content Understanding JSON.</p>

      <label className="record-id">
        Dataverse record id
        <input
          value={recordId}
          onChange={(event) => setRecordId(event.target.value)}
          placeholder="297b01e2-739d-f111-b8db-70a8a5b028cc"
          spellCheck={false}
        />
      </label>

      <div className="option-divider" role="presentation" aria-hidden="true">
        <span>OR</span>
      </div>

      <label className="record-id">
        Blob URL (for PDF) + Content Understanding JSON (below)
        <input
          value={blobUrl}
          onChange={(event) => setBlobUrl(event.target.value)}
          placeholder="https://.../completed/...pdf"
          spellCheck={false}
        />
      </label>

      <p className="or">Paste JSON below (MappedForm, Dataverse record JSON, or Content Understanding result), or drop a .json file anywhere on this panel.</p>
      <textarea
        value={text}
        onChange={(event) => setText(event.target.value)}
        placeholder='{ "status": 2, "payload": { "contents": [ { "fields": { "PolicyholderLastName": { "type": "string", "valueString": "Sample04", "confidence": 0.723, "source": "D(2,...)" } } } ] }, "error": null }'
        spellCheck={false}
      />
      {error && <p className="error">{error}</p>}
      <div className="actions">
        <button type="button" className="primary" onClick={handleLoad} disabled={!canLoad || busy}>
          {busy ? 'Loading…' : 'Load'}
        </button>
      </div>
    </div>
  );
}
