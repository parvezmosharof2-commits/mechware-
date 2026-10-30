import { MechWareApiClient, type ProblemDetails } from '@mechware/api-client';

/** Base URL of the MECHWARE API. Must be same-site with the web app so the refresh cookie is sent. */
export const API_BASE_URL = process.env.NEXT_PUBLIC_API_BASE_URL ?? 'http://localhost:5000';

export const api = new MechWareApiClient({ baseUrl: API_BASE_URL });

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
