import type { ProblemDetails } from './index';

function asProblem(error: unknown): ProblemDetails | null {
  return typeof error === 'object' && error !== null && 'status' in error ? (error as ProblemDetails) : null;
}

export function problemStatus(error: unknown): number | null {
  return asProblem(error)?.status ?? null;
}

export function problemCode(error: unknown): string | null {
  return asProblem(error)?.error?.code ?? null;
}

/** Human-readable message from an API error, never leaking internals. */
export function problemMessage(error: unknown, fallback: string): string {
  const problem = asProblem(error);
  if (!problem) return fallback;
  return problem.error?.message || problem.detail || problem.title || fallback;
}

/** Field-level messages from a 400 validation response, keyed by camelCase field name. */
export function problemFieldErrors(error: unknown): Record<string, string> {
  const details = asProblem(error)?.error?.details ?? [];
  const out: Record<string, string> = {};
  for (const d of details) {
    if (d.field && !out[d.field]) out[d.field] = d.message;
  }
  return out;
}
