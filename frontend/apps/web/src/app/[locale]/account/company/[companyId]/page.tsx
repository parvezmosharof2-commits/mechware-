import { CompanyDashboardView } from '../../../../../components/company/CompanyDashboardView';

export default async function CompanyDashboardPage({ params }: { params: Promise<{ companyId: string }> }) {
  const { companyId } = await params;
  return <CompanyDashboardView companyId={companyId} />;
}
