import { CreditCompanyView } from '../../../../components/CreditCompanyView';

export default async function CreditCompanyPage({ params }: { params: Promise<{ id: string }> }) {
  const { id } = await params;
  return <CreditCompanyView id={id} />;
}
