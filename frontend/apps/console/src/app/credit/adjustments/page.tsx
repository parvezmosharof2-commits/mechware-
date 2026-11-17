import { CreditAdjustmentsView } from '../../../components/CreditAdjustmentsView';

export default async function CreditAdjustmentsPage({ searchParams }: { searchParams: Promise<{ companyId?: string }> }) {
  const { companyId } = await searchParams;
  return <CreditAdjustmentsView companyId={companyId && /^[0-9a-f-]{36}$/i.test(companyId) ? companyId : undefined} />;
}
