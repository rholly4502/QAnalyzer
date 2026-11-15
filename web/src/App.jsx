import { useState } from 'react';

const API = import.meta.env.BASE_URL + 'api/analyze';

export default function App() {
  const [sql, setSql] = useState('SELECT * FROM Orders;');
  const [maxdop, setMaxdop] = useState(false);
  const [profile, setProfile] = useState('general');
  const [result, setResult] = useState(null);
  const [error, setError] = useState(null);
  const [busy, setBusy] = useState(false);

  async function analyze() {
    setBusy(true); setError(null);
    try {
      const r = await fetch(API, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ sql, profile, enableMaxDopTransform: maxdop }),
      });
      const j = await r.json();
      if (!r.ok) throw new Error(j.error || 'Gagal menganalisis');
      setResult(j);
    } catch (e) {
      setError(e.message);
    } finally {
      setBusy(false);
    }
  }

  return (
    <div className="app">
      <h1>🔍 QAnalyzer — Web Demo</h1>
      <p className="muted" style={{ color: '#64748b' }}>
        Static T-SQL analyzer. Core murni (QAnalyzer.Core) dipakai ulang di web.
      </p>

      <div className="card">
        <label htmlFor="sql" style={{ fontWeight: 600 }}>Script T-SQL</label>
        <textarea id="sql" value={sql} onChange={(e) => setSql(e.target.value)} />
        <div className="row">
          <label className="opt">
            <input type="checkbox" checked={maxdop} onChange={(e) => setMaxdop(e.target.checked)} />
            Update resource (MAXDOP 1)
          </label>
          <label className="opt">
            Profile:
            <select value={profile} onChange={(e) => setProfile(e.target.value)}>
              <option value="general">General</option>
              <option value="reporting">Reporting</option>
            </select>
          </label>
          <button onClick={analyze} disabled={busy || !sql.trim()}>
            {busy ? 'Menganalisis…' : 'Analisis'}
          </button>
        </div>
        {error && <p style={{ color: '#dc2626', marginTop: 10 }}>{error}</p>}
      </div>

      {result && (
        <div className="card">
          <div className="row" style={{ marginTop: 0, gap: 8 }}>
            <span className="badge total">Diagnostic: {result.total}</span>
            {result.hasCritical && <span className="badge Critical">Ada Critical</span>}
          </div>
          {result.diagnostics.length === 0 ? (
            <p className="empty">Tidak ada temuan. ✓</p>
          ) : (
            <ul className="diags">
              {result.diagnostics.map((d, i) => (
                <li key={i} className="diag">
                  <div className="t">
                    <span className={'badge ' + d.level}>{d.level}</span>
                    <span>{d.title}</span>
                    {d.line > 0 && <span className="pos">baris {d.line}</span>}
                  </div>
                  <p className="m">{d.message}</p>
                  {d.suggestion && <p className="s">💡 {d.suggestion}</p>}
                </li>
              ))}
            </ul>
          )}
        </div>
      )}

      {result?.transformedSql && (
        <div className="card">
          <div style={{ fontWeight: 600, marginBottom: 8 }}>⚡ Versi hemat resource</div>
          <pre className="transformed">{result.transformedSql}</pre>
        </div>
      )}
    </div>
  );
}
