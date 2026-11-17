import { CreditAdjustRequestView } from '../../../../../components/CreditAdjustRequestView';

export default async function CreditAdjustPage({ params }: { params: Promise<{ id: string }> }) {
  const { id } = await params;
  return <CreditAdjustRequestView id={id} />;
}
