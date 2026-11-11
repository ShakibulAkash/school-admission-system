import React, {useCallback, useEffect, useState} from 'react';

const BASE = (import.meta.env.VITE_API_URL || 'http://localhost:8080').replace(/\/$/, '');
const validId = (id) => /^\d{17}$/.test(id);
const isActive = (hold, now) => hold.status === 'HELD' && new Date(hold.expiresAt).getTime() > now;
const remaining = (date, now) => Math.max(0, Math.ceil((new Date(date).getTime() - now) / 1000));
function prettyTime(seconds) {return `${Math.floor(seconds / 60)}m ${String(seconds % 60).padStart(2, '0')}s`;}
function effectiveStatus(hold, now) { return hold.status === 'HELD' && !isActive(hold, now) ? 'EXPIRED' : hold.status; }

export default function App() {
  const [studentId, setStudentId] = useState(() => localStorage.getItem('school-student-id') || '');
  const [tab, setTab] = useState('classes');
  const [classes, setClasses] = useState([]);
  const [holds, setHolds] = useState([]);
  const [currentId, setCurrentId] = useState('');
  const [newName, setNewName] = useState('');
  const [newSeats, setNewSeats] = useState('120');
  const [busy, setBusy] = useState(false);
  const [notice, setNotice] = useState(null);
  const [now, setNow] = useState(Date.now());

  async function request(path, {method = 'GET', body, student = false} = {}) {
    let result;
    try {
      result = await fetch(`${BASE}${path}`, {
        method,
        headers: {
          ...(body ? {'Content-Type':'application/json'} : {}),
          ...(student ? {'X-Student-Id':studentId} : {})
        },
        ...(body ? {body:JSON.stringify(body)} : {})
      });
    } catch (_) {
      throw new Error('Cannot reach the backend. Check Docker and http://localhost:8080/health.');
    }
    if (result.status === 204) return null;
    let data = null;
    try { data = await result.json(); } catch (_) { /* show a safe error below */ }
    if (!result.ok) throw new Error(data?.error || `Request failed (HTTP ${result.status}).`);
    return data;
  }

  const loadClasses = useCallback(async () => {
    try {
      const resp = await fetch(`${BASE}/classes`);
      if (!resp.ok) throw new Error('Backend unavailable');
      setClasses(await resp.json());
      setNotice(previous => previous?.text === 'Backend is offline. Start Docker or check the API URL.' ? null : previous);
    } catch (_) {
      setNotice(previous => previous || {kind:'error',text:'Backend is offline. Start Docker or check the API URL.'});
    }
  }, []);
  const loadHolds = useCallback(async () => {
    if (!validId(studentId)) { setHolds([]); return; }
    try {
      const resp = await fetch(`${BASE}/me/holds`, {headers:{'X-Student-Id':studentId}});
      if (resp.ok) setHolds(await resp.json());
    } catch (_) { /* keep the last displayed records */ }
  }, [studentId]);

  useEffect(() => {
    loadClasses(); loadHolds();
    const refresh = setInterval(() => {loadClasses(); loadHolds();}, 4000);
    return () => clearInterval(refresh);
  }, [loadClasses, loadHolds]);
  useEffect(() => {const timer = setInterval(() => setNow(Date.now()), 1000); return () => clearInterval(timer);}, []);
  useEffect(() => {localStorage.setItem('school-student-id', studentId);}, [studentId]);

  async function act(callback) {
    setBusy(true); setNotice(null);
    try {await callback(); await Promise.all([loadClasses(), loadHolds()]);}
    catch (error) {setNotice({kind:'error', text:error.message});}
    finally {setBusy(false);}
  }
  function needStudent() {
    if (!validId(studentId)) {setNotice({kind:'error',text:'Enter a valid 17-digit birth registration number first.'}); return false;}
    return true;
  }
  async function createClass(event) {
    event.preventDefault();
    await act(async () => {
      const seats = Number(newSeats);
      if (!newName.trim() || !Number.isInteger(seats) || seats < 1) throw new Error('Enter a class name and a positive whole number of seats.');
      await request('/classes',{method:'POST',body:{name:newName.trim(),totalSeats:seats}});
      setNewName(''); setNewSeats('120'); setTab('classes');
      setNotice({kind:'ok',text:'Class created successfully.'});
    });
  }
  async function apply(classId) {
    if (!needStudent()) return;
    await act(async () => {
      const h = await request(`/classes/${classId}/holds`,{method:'POST',student:true});
      setCurrentId(h.holdId); setTab('application');
      setNotice({kind:'ok',text:'Seat held. Confirm before the countdown expires.'});
    });
  }
  async function confirm(holdId) {
    if (!needStudent()) return;
    await act(async () => {
      await request(`/holds/${holdId}/confirm`,{method:'POST',student:true});
      setNotice({kind:'ok',text:'Admission confirmed. Payment was simulated as required by the assignment.'});
    });
  }
  async function cancel(holdId) {
    if (!needStudent()) return;
    await act(async () => {
      await request(`/holds/${holdId}`,{method:'DELETE',student:true});
      setNotice({kind:'ok',text:'Application cancelled and the seat released.'});
    });
  }

  const activeHold = holds.find(h => h.holdId === currentId) || null;
  const totalClasses = classes.length;
  const availableSeats = classes.reduce((s,c) => s + c.availableSeats, 0);
  const confirmedSeats = classes.reduce((s,c) => s + c.confirmedSeats, 0);

  function holdCard(h) {
    const status = effectiveStatus(h, now);
    return <article className="hold-card" key={h.holdId}>
      <div className="card-heading"><div><h3>{h.className || classes.find(c=>c.id===h.classId)?.name || h.classId}</h3><p className="muted tiny">Application ID: {h.holdId}</p></div><span className={`status ${status.toLowerCase()}`}>{status}</span></div>
      <div className="hold-meta"><span>Class ID: <strong>{h.classId}</strong></span><span>Expiry: <strong>{new Date(h.expiresAt).toLocaleString()}</strong></span></div>
      {status === 'HELD' && <div className="countdown">Time remaining <strong>{prettyTime(remaining(h.expiresAt,now))}</strong></div>}
      {status === 'EXPIRED' && <p className="error-inline">This hold has expired. You can apply again if seats are available.</p>}
      {status === 'CONFIRMED' && <p className="success-inline">Your admission is confirmed.</p>}
      <div className="actions">
        {status === 'HELD' && <><button className="primary" disabled={busy} onClick={()=>confirm(h.holdId)}>Pay Admission Fee (Demo)</button><button className="outline danger" disabled={busy} onClick={()=>cancel(h.holdId)}>Cancel</button></>}
        <button className="outline" onClick={()=>{setCurrentId(h.holdId);setTab('application');}}>View application</button>
      </div>
    </article>;
  }

  return <div className="app">
    <header className="topbar"><div className="shell head"><div className="brand"><div className="brand-icon">S</div><div><strong>School Admission</strong><small>Online Enrollment Portal</small></div></div><div className="identity"><label htmlFor="student-id">Student Birth Registration Number</label><input id="student-id" type="text" inputMode="numeric" value={studentId} onChange={e=>setStudentId(e.target.value.replace(/\D/g,'').slice(0,17))} maxLength={17} placeholder="Enter 17-digit number"/><span className={validId(studentId)?'valid':'invalid'}>{studentId.length}/17 digits</span></div></div></header>
    <main className="shell">
      <section className="hero"><div className="eyebrow">SCHOOL ADMISSION SYSTEM</div><h1>Admissions made simple,<br/><em>fair and transparent.</em></h1><p>Apply on a first-come, first-served basis. Seats are held temporarily and confirmed after a simulated fee payment.</p></section>
      <section className="stats"><div className="stat"><span>Available classes</span><strong>{totalClasses}</strong></div><div className="stat"><span>Available seats</span><strong>{availableSeats}</strong></div><div className="stat"><span>Confirmed admissions</span><strong>{confirmedSeats}</strong></div></section>
      <nav className="tabs" aria-label="Pages">
        {[['classes','Class List'],['application','Application'],['mine','My Applications'],['admin','Create Class (Admin)']].map(([id,title])=><button key={id} onClick={()=>{setTab(id);setNotice(null);}} className={tab===id?'selected':''}>{title}</button>)}
      </nav>
      {notice && <div role="alert" className={`notice ${notice.kind}`}>{notice.text}<button onClick={()=>setNotice(null)} aria-label="Dismiss">×</button></div>}
      {tab==='classes' && <section className="section"><div className="section-title"><div><h2>Available Classes & Shifts</h2><p>Seats update automatically as applications arrive and expire.</p></div><button className="outline" onClick={loadClasses}>Refresh</button></div><div className="class-grid">{classes.map(c=><article className="class-card" key={c.id}><div className="class-label">ADMISSION OPEN</div><h3>{c.name}</h3><div className="seat-row"><span>Available seats</span><strong>{c.availableSeats} / {c.totalSeats}</strong></div><div className="track"><div style={{width:`${100*(c.heldSeats+c.confirmedSeats)/Math.max(1,c.totalSeats)}%`}}/></div><div className="seat-mini"><span>On hold <b>{c.heldSeats}</b></span><span>Confirmed <b>{c.confirmedSeats}</b></span></div><button className="primary full" disabled={busy || c.availableSeats<=0} onClick={()=>apply(c.id)}>{c.availableSeats<=0?'Fully booked':'Apply Now'}</button></article>)}{classes.length===0&&<div className="empty">No classes yet. Open the <button onClick={()=>setTab('admin')}>Admin screen</button> to create one.</div>}</div></section>}
      {tab==='application' && <section className="section"><div className="section-title"><div><h2>Application Details</h2><p>Track your current seat hold or confirmed admission.</p></div></div>{activeHold?holdCard(activeHold):<div className="empty"><p>No application selected.</p><button className="primary" onClick={()=>setTab('mine')}>View My Applications</button></div>}</section>}
      {tab==='mine' && <section className="section"><div className="section-title"><div><h2>My Applications</h2><p>All applications associated with the entered birth registration number.</p></div><button className="outline" onClick={loadHolds}>Refresh</button></div>{!validId(studentId)?<div className="empty">Enter your 17-digit birth registration number at the top to load your applications.</div>:holds.length===0?<div className="empty">No applications found for this student.</div>:<div className="holds-grid">{holds.map(holdCard)}</div>}</section>}
      {tab==='admin' && <section className="section"><div className="section-title"><div><h2>Create Admission Class</h2><p>Each shift is a separate class with its own seat capacity.</p></div></div><form className="admin-form" onSubmit={createClass}><label>Class / shift name<input value={newName} onChange={e=>setNewName(e.target.value)} placeholder="e.g. Class 6 - Morning Shift" required maxLength={120}/></label><label>Total seats<input value={newSeats} onChange={e=>setNewSeats(e.target.value)} type="number" min="1" required/></label><button disabled={busy} className="primary">Create Class</button><p className="muted tiny">No login is required in this demo, as specified in the assignment. Real deployments must restrict administrator access.</p></form></section>}
    </main><footer>School Admission System · First come, first served · Demo payment only</footer>
  </div>;
}
