import { createCspReportHandler } from '@mechware/config/csp-report';

/** Browsers post CSP violation reports here (threat model M5); they are cleaned, capped and logged. */
export const POST = createCspReportHandler('console');
