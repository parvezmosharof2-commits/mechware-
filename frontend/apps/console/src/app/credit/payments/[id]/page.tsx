import { CreditPaymentDetailView } from '../../../../components/CreditPaymentDetailView';

export default async function CreditPaymentPage({ params }: { params: Promise<{ id: string }> }) {
  const { id } = await params;
  return <CreditPaymentDetailView id={id} />;
}
