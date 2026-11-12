import { AuthFlowSurface } from '../../../../components/auth/AuthModal';

interface RegisterPageProps {
  params: Promise<{ locale: string }>;
}

export default async function RegisterPage({ params }: RegisterPageProps) {
  const resolvedParams = await params;
  const locale = resolvedParams.locale === 'bn' ? 'bn' : 'en';

  return (
    <main id="main" className="flex min-h-[70vh] flex-col items-center justify-center bg-bg-canvas p-6">
      <AuthFlowSurface locale={locale} initialMode="signup" />
    </main>
  );
}
