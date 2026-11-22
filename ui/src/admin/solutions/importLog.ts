import { t } from '../../shell/i18n/t';

export interface ImportLogSummary {
  failures: { code: string; text: string }[];
  warnings: number;
}

/**
 * Summarizes `importjob.data` (root `importexportxml`; one `result` element per processed component with `result` =
 * success, warning or failure, plus `errorcode` and `errortext`): the failures with their code and text, and the number
 * of warnings. Throws when the text is not XML.
 */
export function summarizeImportLog(xml: string): ImportLogSummary {
  const doc = new DOMParser().parseFromString(xml, 'application/xml');
  if (doc.getElementsByTagName('parsererror').length > 0) throw new Error(t('solutions.import.logInvalid'));
  const summary: ImportLogSummary = { failures: [], warnings: 0 };
  for (const result of Array.from(doc.getElementsByTagName('result'))) {
    const kind = result.getAttribute('result');
    if (kind === 'warning') summary.warnings++;
    if (kind === 'failure') {
      summary.failures.push({ code: result.getAttribute('errorcode') ?? '', text: result.getAttribute('errortext') ?? '' });
    }
  }
  return summary;
}

/** Reads a file as base64 (the `CustomizationFile` of ImportSolutionAsync). */
export function readBase64(file: Blob): Promise<string> {
  return new Promise((resolve, reject) => {
    const reader = new FileReader();
    reader.onerror = () => reject(reader.error ?? new Error(t('solutions.import.readFailed')));
    reader.onload = () => {
      const url = String(reader.result);
      resolve(url.slice(url.indexOf(',') + 1));
    };
    reader.readAsDataURL(file);
  });
}
