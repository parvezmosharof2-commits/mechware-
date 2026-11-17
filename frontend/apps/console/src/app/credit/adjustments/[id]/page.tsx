import { CreditAdjustmentDetailView } from '../../../../components/CreditAdjustmentDetailView';

export default async function CreditAdjustmentPage({ params }: { params: Promise<{ id: string }> }) {
  const { id } = await params;
  return <CreditAdjustmentDetailView id={id} />;
}
