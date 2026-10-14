import { Server, type Tool } from '@modelcontextprotocol/server';
import { StdioServerTransport } from '@modelcontextprotocol/server/stdio';
import { protectedJson, loopbackUrl } from '../mcp-server/discovery.js';
import { AutomationError, failure } from '../contracts/errors.js';

const tools:Tool[]=[
  {name:'ask_user',description:'Ask the native OpenUtau user a bounded question. Return a pending questionId; call read_answer to receive the answer.',inputSchema:{type:'object',properties:{question:{type:'string',minLength:1,maxLength:8192},title:{type:'string',maxLength:256},options:{type:'array',maxItems:16,items:{type:'object',properties:{optionId:{type:'string'},name:{type:'string'}},required:['optionId','name'],additionalProperties:false}}},required:['question'],additionalProperties:false},annotations:{readOnlyHint:true,idempotentHint:false,openWorldHint:false}},
  {name:'read_answer',description:'Read a native user answer with an optional bounded, cancellable wait (0–30000 ms).',inputSchema:{type:'object',properties:{questionId:{type:'string'},timeoutMs:{type:'integer',minimum:0,maximum:30000}},required:['questionId'],additionalProperties:false},annotations:{readOnlyHint:true,idempotentHint:true,openWorldHint:false}}
];
async function start(){
  const server=new Server({name:'openutau-assistant',version:'1.0.0'},{capabilities:{tools:{}}});
  server.setRequestHandler('tools/list',async()=>({tools}));
  server.setRequestHandler('tools/call',async(request,ctx)=>{
    let result:Record<string,unknown>;
    try{
      const path=process.env.OPENUTAU_ASSISTANT_HELPER_REF;
      if(!path)throw new AutomationError('UNAUTHORIZED','The assistant helper is available only in a native session.');
      const ref=await protectedJson(path);
      if(typeof ref.credential!=='string'||typeof ref.sessionId!=='string')throw new AutomationError('UNAUTHORIZED','Invalid assistant helper reference.');
      const url=loopbackUrl(ref.url);
      if(!['ask_user','read_answer'].includes(request.params.name))throw new AutomationError('INVALID_ARGUMENT','Unknown assistant tool.');
      const body={...(request.params.arguments||{}),sessionId:ref.sessionId,processGeneration:ref.processGeneration};
      const response=await fetch(`${url}/${request.params.name==='ask_user'?'ask':'read'}`,{method:'POST',headers:{authorization:`Bearer ${ref.credential}`,'content-type':'application/json'},body:JSON.stringify(body),redirect:'error',signal:AbortSignal.any([ctx.mcpReq.signal,AbortSignal.timeout(35000)])});
      result=await response.json() as Record<string,unknown>;
    }catch(error){result=failure(error);}
    return {isError:result.ok===false,structuredContent:result,content:[{type:'text',text:JSON.stringify(result)}]};
  });await server.connect(new StdioServerTransport());
}
start().catch(()=>{process.stderr.write('OpenUtau assistant helper failed to start.\n');process.exitCode=1;});
