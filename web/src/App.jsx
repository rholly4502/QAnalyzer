import { useState } from 'react';

const API = import.meta.env.BASE_URL + 'api/analyze';
const MAX_PLAN_LENGTH = 100_000;
const SEVERITY_ORDER = ['Critical', 'Warning', 'Info'];

function buildMarkdown(diagnostics) {
  const lines = ['# QAnalyzer Report'];
  for (const level of SEVERITY_ORDER) {
    const items = diagnostics.filter((d) => d.level === level);
    if (items.length === 0) continue;
    lines.push('', `## ${level}`);
    for (const d of items) {
      const suggestion = d.suggestion ? ` → *${d.suggestion}*` : '';
      lines.push(`- **[Line ${d.line}]** ${d.ruleId}: ${d.message}${suggestion}`);
    }
  }
  return lines.join('\n') + '\n';
}

export default function App() {
  const [sql, setSql] = useState('SELECT * FROM Orders;');
  const [sqlPlanXml, setSqlPlanXml] = useState('');
  const [planFileName, setPlanFileName] = useState('');
  const [showPlanInput, setShowPlanInput] = useState(false);
  const [maxdop, setMaxdop] = useState(false);
  const [profile, setProfile] = useState('general');
  const [filters, setFilters] = useState({ Critical: true, Warning: true, Info: true });
  const [result, setResult] = useState(null);
  const [error, setError] = useState(null);
  const [busy, setBusy] = useState(false);
  const [copied, setCopied] = useState('');

  async function handlePlanFile(event) {
    const file = event.target.files?.[0];
    event.target.value = '';
    if (!file) return;

    try {
      const text = await file.text();
      if (text.length > MAX_PLAN_LENGTH) {
        setError(`File execution plan melebihi batas maksimal ${MAX_PLAN_LENGTH.toLocaleString('id-ID')} karakter.`);
        return;
      }
      setSqlPlanXml(text);
      setPlanFileName(file.name);
      setShowPlanInput(true);
      setError(null);
    } catch {
      setError('Gagal membaca file execution plan.');
    }
  }

  function clearPlan() {
    setSqlPlanXml('');
    setPlanFileName('');
  }

  async function copyText(kind, text) {
    try {
      await navigator.clipboard.writeText(text);
      setCopied(kind);
      setTimeout(() => setCopied((prev) => (prev === kind ? '' : prev)), 2000);
    } catch {
      setError('Tidak dapat menyalin ke clipboard di browser ini.');
    }
  }

  async function analyze() {
    setBusy(true);
    setError(null);
    setCopied('');
    try {
      const r = await fetch(API, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({
          sql,
          profile,
          enableMaxDopTransform: maxdop,
          sqlPlanXml: sqlPlanXml.trim() ? sqlPlanXml : null,
        }),
      });
      const raw = await r.text();
      let j = null;
      if (raw) {
        try {
          j = JSON.parse(raw);
        } catch {
          j = null;
        }
      }
      if (!r.ok) throw new Error(j?.error || raw || 'Gagal menganalisis');
      if (!j) throw new Error('Respons server tidak valid.');
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

  const visibleDiagnostics = result
    ? result.diagnostics.filter((d) => filters[d.level] ?? true)
    : [];

  return (
    <div className="app">
      <h1>🔍 QAnalyzer — Web Demo</h1>
      <p className="muted" style={{ color: '#64748b' }}>
        Static T-SQL &amp; .sqlplan analyzer. Core murni (QAnalyzer.Core) dipakai ulang di web.
      </p>

      <div className="card">
        <label htmlFor="sql" style={{ fontWeight: 600 }}>Script T-SQL</label>
        <textarea id="sql" value={sql} onChange={(e) => setSql(e.target.value)} spellCheck={false} />

        <div className="plan-section">
          <div className="plan-toolbar">
            <button
              type="button"
              className="btn-secondary"
              onClick={() => setShowPlanInput((prev) => !prev)}
            >
              {showPlanInput || sqlPlanXml ? '▾' : '▸'} XML Execution Plan (.sqlplan) — opsional
            </button>
            <label className="btn-secondary file-btn">
              Unggah .sqlplan / .xml
              <input
                type="file"
                accept=".sqlplan,.xml,text/xml,application/xml"
                onChange={handlePlanFile}
                className="sr-only"
              />
            </label>
            {sqlPlanXml && (
              <>
                <span className="plan-pill">
                  {planFileName ? `${planFileName} ` : ''}({sqlPlanXml.length.toLocaleString('id-ID')} karakter)
                </span>
                <button type="button" className="btn-link" onClick={clearPlan}>
                  Hapus Plan
                </button>
              </>
            )}
          </div>

          {(showPlanInput || sqlPlanXml) && (
            <textarea
              id="sqlplan"
              className="plan-input"
              value={sqlPlanXml}
              onChange={(e) => {
                setSqlPlanXml(e.target.value);
                if (!e.target.value) setPlanFileName('');
              }}
              placeholder="Tempel isi file .sqlplan (<ShowPlanXML>...</ShowPlanXML>) untuk mendeteksi Missing Index, Key Lookup, Table Scan, atau Spill to TempDb..."
              spellCheck={false}
            />
          )}
        </div>

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
          <button onClick={analyze} disabled={busy || (!sql.trim() && !sqlPlanXml.trim())}>
            {busy ? 'Menganalisis…' : 'Analisis'}
          </button>
        </div>
        {error && <p className="error-msg">{error}</p>}
      </div>

      {result && (
        <div className="card">
          <div className="row" style={{ marginTop: 0, gap: 8 }}>
            <span className="badge total">Diagnostic: {result.total}</span>
            {counts.Critical > 0 && <span className="badge Critical">Critical: {counts.Critical}</span>}
            {counts.Warning > 0 && <span className="badge Warning">Warning: {counts.Warning}</span>}
            {counts.Info > 0 && <span className="badge Info">Info: {counts.Info}</span>}
            {result.hasExecutionPlan && (
              <span className="badge Plan">
                {result.planDiagnosticCount > 0
                  ? `Execution Plan: ${result.planDiagnosticCount} temuan`
                  : 'Execution Plan ✓'}
              </span>
            )}
            {result.hasCritical && <span className="badge Critical">Ada Critical</span>}
          </div>

          {result.diagnostics.length > 0 && (
            <div className="filter-row">
              <span className="filter-label">Filter:</span>
              {SEVERITY_ORDER.map((level) => (
                <label key={level} className="opt">
                  <input
                    type="checkbox"
                    checked={filters[level]}
                    onChange={(e) => setFilters((prev) => ({ ...prev, [level]: e.target.checked }))}
                  />
                  {level}
                </label>
              ))}
              <button
                type="button"
                className="btn-secondary ml-auto"
                disabled={visibleDiagnostics.length === 0}
                onClick={() => copyText('md', buildMarkdown(visibleDiagnostics))}
              >
                {copied === 'md' ? 'Markdown Tersalin ✓' : 'Salin Markdown'}
              </button>
            </div>
          )}

          {result.diagnostics.length === 0 ? (
            <p className="empty">Tidak ada temuan. ✓</p>
          ) : visibleDiagnostics.length === 0 ? (
            <p className="empty">Semua temuan disembunyikan oleh filter level.</p>
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
                  {visibleDiagnostics.map((d, i) => (
                    <tr key={`${d.ruleId}-${d.line}-${d.column}-${i}`} className={`sev-${d.level.toLowerCase()}`}>
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
          <div className="card-header">
            <div style={{ fontWeight: 600 }}>⚡ Versi hemat resource</div>
            <button
              type="button"
              className="btn-secondary"
              onClick={() => copyText('sql', result.transformedSql)}
            >
              {copied === 'sql' ? 'SQL Tersalin ✓' : 'Salin SQL'}
            </button>
          </div>
          <pre className="transformed">{result.transformedSql}</pre>
        </div>
      )}
    </div>
  );
}
