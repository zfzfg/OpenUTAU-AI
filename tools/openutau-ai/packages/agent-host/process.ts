import { spawn, execFile, type ChildProcessWithoutNullStreams } from 'node:child_process';
import { once } from 'node:events';
import { access, stat, readFile } from 'node:fs/promises';
import { constants } from 'node:fs';
import { isAbsolute, join, dirname, delimiter } from 'node:path';
import { promisify } from 'node:util';
import { AutomationError } from '../contracts/errors.js';

const execFileAsync=promisify(execFile);
export async function locateExecutable(name:string,configured?:string):Promise<string>{
  if(configured&&!isAbsolute(configured))throw new AutomationError('INVALID_ARGUMENT','A configured provider path must be absolute.');
  const candidates=configured?[configured]:(process.env.PATH||'').split(delimiter).flatMap(folder=>process.platform==='win32'?[join(folder,`${name}.exe`),join(folder,`${name}.cmd`),join(folder,name)]:[join(folder,name)]);
  for(const path of candidates){try{if(!(await stat(path)).isFile())continue;await access(path,process.platform==='win32'?constants.F_OK:constants.X_OK);return path;}catch{}}
  throw new AutomationError('CLI_MISSING',`The ${name} program is not installed or its configured path is unavailable.`);
}
export async function executableSpec(path:string,args:string[],env:NodeJS.ProcessEnv):Promise<{command:string;args:string[];env:NodeJS.ProcessEnv}>{
  if(/\.(c?js|mjs)$/i.test(path))return {command:process.execPath,args:[path,...args],env};
  if(/\.(cmd|bat)$/i.test(path)){
    // Resolve only the standard npm-generated wrapper to a concrete JS entry; never execute arbitrary shell text.
    const wrapper=await readFile(path,'utf8');
    const match=/%dp0%\\([^"\r\n]+\.(?:js|cjs|mjs))/i.exec(wrapper);
    if(!match)throw new AutomationError('NATIVE_EXECUTABLE_REQUIRED','Choose a native executable or the installed adapter JavaScript entry instead of a shell wrapper.');
    const entry=join(dirname(path),match[1]);if(!(await stat(entry)).isFile())throw new AutomationError('ADAPTER_MISSING','The installed npm wrapper has no JavaScript entry.');
    return {command:process.execPath,args:[entry,...args],env};
  }
  return {command:path,args,env};
}
export async function startProcess(spec:{command:string;args:string[];env:NodeJS.ProcessEnv},cwd:string):Promise<ChildProcessWithoutNullStreams>{
  if(!isAbsolute(cwd)||(await stat(cwd)).isDirectory()===false)throw new AutomationError('INVALID_ARGUMENT','The provider working directory must exist and be absolute.');
  const child=spawn(spec.command,spec.args,{env:spec.env,cwd,shell:false,windowsHide:true,detached:process.platform!=='win32',stdio:['pipe','pipe','pipe']});
  await once(child,'spawn');return child;
}
export async function versionOf(path:string):Promise<string|undefined>{
  try{const spec=await executableSpec(path,['--version'],process.env);const result=await execFileAsync(spec.command,spec.args,{env:spec.env,windowsHide:true,timeout:5000,maxBuffer:4096});return result.stdout.trim().slice(0,200)||undefined;}catch{return undefined;}
}
const terminations=new WeakMap<ChildProcessWithoutNullStreams,Promise<void>>();
export function stopOwnedProcess(child:ChildProcessWithoutNullStreams):Promise<void>{
  const current=terminations.get(child);if(current)return current;
  const pending=terminateOwnedProcess(child);terminations.set(child,pending);return pending;
}
async function terminateOwnedProcess(child:ChildProcessWithoutNullStreams):Promise<void>{
  if(!child.pid)return;
  const closed=child.exitCode!==null||child.signalCode!==null;
  if(process.platform==='win32'){
    if(!closed){try{await execFileAsync('taskkill.exe',['/PID',String(child.pid),'/T','/F'],{windowsHide:true,timeout:5000,maxBuffer:4096});}catch{if(child.exitCode===null&&child.signalCode===null)await confirmedClose(child);}}
  }else{
    try{process.kill(-child.pid,'SIGTERM');}catch(error){if((error as NodeJS.ErrnoException).code!=='ESRCH')throw error;}
    const deadline=Date.now()+3000;
    while(Date.now()<deadline){try{process.kill(-child.pid,0);}catch(error){if((error as NodeJS.ErrnoException).code==='ESRCH')return;throw error;}await new Promise(resolve=>setTimeout(resolve,25));if(Date.now()>deadline-1500){try{process.kill(-child.pid,'SIGKILL');}catch{}}}
    throw new AutomationError('TERMINATION_UNCONFIRMED','The owned provider process group did not exit; reconnect is blocked.');
  }
  if(child.exitCode===null&&child.signalCode===null)await confirmedClose(child);
}
async function confirmedClose(child:ChildProcessWithoutNullStreams){
  let timer:NodeJS.Timeout|undefined;
  try{await Promise.race([once(child,'close'),new Promise<never>((_,reject)=>{timer=setTimeout(()=>reject(new AutomationError('TERMINATION_UNCONFIRMED','The owned provider process did not exit.')),3000);})]);}
  finally{if(timer)clearTimeout(timer);}
}
