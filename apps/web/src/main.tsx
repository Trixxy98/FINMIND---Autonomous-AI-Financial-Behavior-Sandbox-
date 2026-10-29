import React from 'react';
import { createRoot } from 'react-dom/client';

function App() {
  return (
    <main>
      <h1>FINMIND</h1>
      <p>Virtual-funds research sandbox</p>
      <p>Phase 0 scaffold — no financial operations are enabled.</p>
    </main>
  );
}

createRoot(document.getElementById('root')!).render(
  <React.StrictMode>
    <App />
  </React.StrictMode>,
);
