import { JsonLineReader, type RpcFrame } from './json-rpc.js';
import { AgentHost } from './host.js';
import { AutomationError } from '../contracts/errors.js';
import { providerError } from './providers.js';

function write(frame:RpcFrame){process.stdout.write(JSON.stringify(frame)+'\n');}
const host=new AgentHost((method,params)=>write({jsonrpc:'2.0',method,params}));
let closing=false;
async function close(){if(closing)return;closing=true;try{await host.dispose();}catch{process.stderr.write('OpenUtau assistant cleanup did not confirm all owned process exits.\n');process.exitCode=1;}}
new JsonLineReader(process.stdin,frame=>{
  if(!frame.method)return;
  void host.dispatch(frame.method,frame.params||{}).then(result=>{if(frame.id!==undefined&&frame.id!==null)write({jsonrpc:'2.0',id:frame.id,result});if(frame.method==='shutdown'){void close().then(()=>process.stdin.destroy());}},error=>{
    if(frame.id!==undefined&&frame.id!==null)write({jsonrpc:'2.0',id:frame.id,error:{code:error instanceof AutomationError&&error.code==='METHOD_NOT_FOUND'?-32601:-32000,message:providerError(error).message,data:{code:error instanceof AutomationError?error.code:'PROVIDER_ERROR'}}});
    else write({jsonrpc:'2.0',method:'error',params:providerError(error)});
  });
},()=>{write({jsonrpc:'2.0',method:'error',params:{code:'PROTOCOL_MISMATCH',message:'Invalid or oversized assistant IPC frame.',recoverable:false}});void close().then(()=>process.stdin.destroy());},()=>{void close();});
process.once('SIGTERM',()=>{void close().then(()=>process.exit());});
process.once('SIGINT',()=>{void close().then(()=>process.exit());});
