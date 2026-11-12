/** Company dashboard tabs a link may open with `?tab=` (the approval email links to `?tab=approvals`). */
export type DashboardTab = 'sites' | 'costCentres' | 'approvals' | 'members';

const TABS: readonly DashboardTab[] = ['sites', 'costCentres', 'approvals', 'members'];

/** `?tab=` value → tab; unknown or empty values open Sites, a repeated one uses the first. Members stays hidden for non-admins in the view. */
export function parseDashboardTab(value: string | string[] | undefined): DashboardTab {
  const v = Array.isArray(value) ? value[0] : value;
  return TABS.find((t) => t === v) ?? 'sites';
}
