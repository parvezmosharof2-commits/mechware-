import { withholdingErrorText } from '@mechware/api-client';
import { problemCode, problemMessage, problemStatus } from './api';

export const withholdingFailure = (err: unknown): { text: string; code: string | null } => {
  const code = problemCode(err);
  return { text: withholdingErrorText({ status: problemStatus(err), code, message: problemMessage(err, '') }), code };
};
