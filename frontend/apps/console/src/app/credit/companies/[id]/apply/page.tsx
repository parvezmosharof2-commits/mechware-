import { CreditApplyView } from '../../../../../components/CreditApplyView';

export default async function CreditApplyPage({ params }: { params: Promise<{ id: string }> }) {
  const { id } = await params;
  return <CreditApplyView id={id} />;
}
