import { deskErrorText, isStepUpRefusal, issueErrorText, shouldReloadAfterIssue, shouldReloadDesk } from '@mechware/api-client';
import { problemCode, problemFieldErrors, problemMessage, problemStatus } from './api';

/**
 * Plain words for a failed send, approve, reject, recall or withdraw. `stepUp` means the code was refused and the dialog stays open for the next
 * one; `reload` means the screen shows an old state; `message` is the server's own text (the flags-changed refusal names the lines in it).
 */
export function issueError(err: unknown): { text: string; code: string | null; status: number | null; fields: Record<string, string>; message: string; stepUp: boolean; reload: boolean; retry: boolean } {
  const status = problemStatus(err);
  const code = problemCode(err);
  const fields = problemFieldErrors(err);
  const message = problemMessage(err, '');
  const text = issueErrorText({ status, code, message, fields }) ?? deskErrorText({ status, code, message, fields });
  return { text, code, status, fields, message, stepUp: isStepUpRefusal(code), reload: shouldReloadAfterIssue(code) || shouldReloadDesk(code), retry: status === null || status >= 500 };
}
