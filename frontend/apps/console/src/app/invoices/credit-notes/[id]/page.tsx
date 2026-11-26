import { CreditNoteDecisionView } from '../../../../components/CreditNoteDecisionView';

export default async function CreditNoteDecisionPage({ params }: { params: Promise<{ id: string }> }) {
  const { id } = await params;
  return <CreditNoteDecisionView id={id} />;
}
