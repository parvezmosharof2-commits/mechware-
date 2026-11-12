import { CompanyDashboardView } from '../../../../../components/company/CompanyDashboardView';
import { parseDashboardTab } from '../../../../../lib/dashboard-tab';

export default async function CompanyDashboardPage({
  params,
  searchParams,
}: {
  params: Promise<{ companyId: string }>;
  searchParams: Promise<{ tab?: string | string[] }>;
}) {
  const { companyId } = await params;
  const { tab } = await searchParams;
  return <CompanyDashboardView companyId={companyId} initialTab={parseDashboardTab(tab)} />;
}
