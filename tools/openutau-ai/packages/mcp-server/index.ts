import { Server, type Tool } from '@modelcontextprotocol/server';
import { StdioServerTransport } from '@modelcontextprotocol/server/stdio';
import Ajv2020 from 'ajv/dist/2020.js';
import catalog from '../../../../contracts/automation/v1/tools.json' with {type:'json'};
import { failure, AutomationError } from '../contracts/errors.js';
import { EditorClient } from './editor-client.js';

export async function startMcp():Promise<Server>{
  const server=new Server({name:'openutau',version:'1.0.0'},{capabilities:{tools:{listChanged:false}}});
  const client=new EditorClient();
  const ajv=new Ajv2020.default({strict:false,allErrors:true});
  const validators=new Map(catalog.tools.map(tool=>[tool.name,ajv.compile(tool.inputSchema)]));
  server.setRequestHandler('tools/list',async()=>({tools:catalog.tools as unknown as Tool[]}));
  server.setRequestHandler('tools/call',async(request,ctx)=>{
    const params=request.params;
    const validator=validators.get(params.name);
    let result;
    try {
      if(!validator)throw new AutomationError('INVALID_ARGUMENT','Unknown OpenUtau tool.');
      const args=params.arguments||{};
      if(!validator(args))throw new AutomationError('INVALID_ARGUMENT','Tool arguments do not match the published schema.',{validation:validator.errors?.map(x=>({path:x.instancePath,keyword:x.keyword,message:x.message}))});
      result=await client.invoke(params.name.slice('openutau_'.length),args,ctx.mcpReq.signal);
    }catch(error){result=failure(error);}
    return {isError:!result.ok,structuredContent:result,content:[{type:'text',text:JSON.stringify(result)}]};
  });
  await server.connect(new StdioServerTransport());
  return server;
}
startMcp().catch(()=>{process.stderr.write('OpenUtau MCP failed to start.\n');process.exitCode=1;});
