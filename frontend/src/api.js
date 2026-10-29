export function query(filters) {
  return new URLSearchParams(Object.entries(filters).filter(([,v])=>v!=='' && v!==null && v!==undefined)).toString();
}
export async function request(path, options={}) {
  const r=await fetch('/api/'+path,options);
  if(!r.ok){const body=await r.json().catch(()=>({}));throw new Error([body.detail||`Ошибка ${r.status}`,...(body.errors instanceof Array?body.errors:[])].join('\n'));}
  return r.status===204?null:r.json();
}
export const json=(method,data)=>({method,headers:{'Content-Type':'application/json'},body:JSON.stringify(data)});
