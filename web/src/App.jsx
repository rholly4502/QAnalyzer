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

  const counts = result ? result.diagnostics.reduce((acc, d) => {
    acc[d.level] = (acc[d.level] || 0) + 1;
    return acc;
  }, {}) : {};

  return (
    <div className="app">
      <h1>🔍 QAnalyzer — Web Demo</h1>
      <p className="muted" style={{ color: '#64748b' }}>
        Static T-SQL analyzer. Core murni (QAnalyzer.Core) dipakai ulang di web.
      </p>

      <div className="card">
        <label htmlFor="sql" style={{ fontWeight: 600 }}>Script T-SQL</label>
        <textarea id="sql" value={sql} onChange={(e) => setSql(e.target.value)} spellCheck={false} />
        <div className="row">
          <label className="opt">
            <input type="checkbox" checked={maxdop} onChange={(e) => setMaxdop(e.target.checked)} />
            Hemat resource (MAXDOP 1)
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
            {counts.Critical > 0 && <span className="badge Critical">Critical: {counts.Critical}</span>}
            {counts.Warning > 0 && <span className="badge Warning">Warning: {counts.Warning}</span>}
            {counts.Info > 0 && <span className="badge Info">Info: {counts.Info}</span>}
            {result.hasCritical && <span className="badge Critical">Ada Critical</span>}
          </div>

          {result.diagnostics.length === 0 ? (
            <p className="empty">Tidak ada temuan. ✓</p>
          ) : (
            <div className="tablewrap">
              <table className="diags">
                <thead>
                  <tr>
                    <th>Severity</th>
                    <th>Aturan</th>
                    <th>Temuan</th>
                    <th>Baris</th>
                  </tr>
                </thead>
                <tbody>
                  {result.diagnostics.map((d, i) => (
                    <tr key={i} className={`sev-${d.level.toLowerCase()}`}>
                      <td><span className={'badge ' + d.level}>{d.level}</span></td>
                      <td>
                        <div className="tt">{d.title}</div>
                        <div className="rid">{d.ruleId}</div>
                      </td>
                      <td>
                        <div className="m">{d.message}</div>
                        {d.suggestion && <div className="s">💡 {d.suggestion}</div>}
                      </td>
                      <td className="pos">{d.line > 0 ? d.line : '—'}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
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
