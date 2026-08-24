/** Mirrors the C# records in DocumentIntake.Functions/Models/MappedForm.cs. */

export interface BoundingBox {
  page: number;
  polygon: number[];
}

export interface FieldValue {
  column: string;
  value: string | null;
  confidence: number;
  boundingBox: BoundingBox | null;
  type: string;
}

export interface MappedForm {
  formType: string;
  sourceBlobName: string;
  completedBlobUrl: string | null;
  processedUtc: string;
  classificationConfidence: number;
  fields: FieldValue[];
}

export type CoordinateUnits = 'inches' | 'normalized';
export type UnitMode = 'auto' | CoordinateUnits;
