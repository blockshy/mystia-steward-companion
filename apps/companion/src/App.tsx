import { ModWorkbench } from '@/companion/ModWorkbench';

export default function App() {
  return (
    <div className="companion-shell min-h-screen">
      <main className="companion-main mx-auto max-w-[1360px] p-1.5 md:p-2">
        <ModWorkbench />
      </main>
    </div>
  );
}
