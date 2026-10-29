const fs = require('fs');
const vm = require('vm');
const assert = require('assert');

class Element {
  constructor(tag='div') { this.tag=tag; this.children=[]; this.listeners={}; this.hidden=false; this.dataset={}; this.value=''; this.classList={toggle:()=>{}}; }
  append(...items){this.children.push(...items)}
  replaceChildren(){this.children=[]}
  addEventListener(type,fn){this.listeners[type]=fn}
  click(){return this.listeners.click?.({preventDefault(){}})}
  showModal(){this.open=true}
  close(){this.open=false}
}
const html=fs.readFileSync('wwwroot/index.html','utf8'), js=fs.readFileSync('wwwroot/app.js','utf8');
const elements={}; for(const match of html.matchAll(/id="([^"]+)"/g))elements[match[1]]=new Element();
elements.roleKind.value='agent';
const views=['overview','orders','detail','approvals','policies','roles','agents','audit','catalog','connectors'];
const nav=views.filter(x=>x!=='detail').map(x=>{const e=new Element('button');e.dataset.view=x;return e});
const links=['orders','approvals','audit','catalog','connectors'].map(x=>{const e=new Element('button');e.dataset.go=x;return e});
const document={
 getElementById(id){assert(elements[id],'unknown element: '+id);return elements[id]},
 createElement(tag){return new Element(tag)},
 createTextNode(text){return {textContent:text}},
 querySelectorAll(selector){return selector==='.view'?views.map(x=>{let e=elements['view-'+x];e.id='view-'+x;return e}):selector==='[data-view]'?nav:selector==='[data-go]'?links:selector==='.nav-item'?nav:[]},
 head:new Element('head')
};
const samples={
 '/api/auth/csrf':{token:'test-csrf-token'},
 '/api/auth/session':{authenticated:true,username:'operator',configured:true},
 '/api/status':{paypal:false,ai:true,operatorConfigured:true,clientId:''},
 '/api/products':[{id:'p1',name:'Demo item',price:49,currency:'PLN'}],
 '/api/orders':[{id:'order-123456',productId:'p1',productName:'Demo item',price:49,currency:'PLN',providerOrderId:'sandbox-1',providerCaptureId:'capture-1',status:'PAID'}],
 '/api/proposals':[{id:'proposal-123',orderId:'order-123456',reason:'Check fulfillment.',status:'PENDING',policyVersion:1}],
 '/api/action-requests':[],
 '/api/audit':[{at:'2026-10-28T12:00:00Z',orderId:'order-123456',event:'policy.evaluated',actor:'gatekeeper',reason:'Human approval required',source:'approval-required',reference:'proposal-123'}],
 '/api/audit/verify':{isValid:true,checkedEvents:1,hasLegacyEvents:false,failedEventId:null,failure:null},
 '/api/policies/followup':{action:'create_internal_followup',mode:'approval_required',version:1},
 '/api/tasks':[{id:'task-1',proposalId:'proposal-123',orderId:'order-123456',description:'Check fulfillment.',status:'OPEN'}],
 '/api/roles':[],
 '/api/agents':[],
 '/api/connectors':[{id:'paypal',role:'Payments',provider:'PayPal Orders API',status:'not-configured',detail:'Sandbox only'}]
};
let lastPost;
const fetch=async(url,options)=>{if(options){lastPost={url,options};if(url==='/api/auth/login')samples['/api/auth/session'].authenticated=true;if(url==='/api/auth/logout')samples['/api/auth/session'].authenticated=false;return {ok:true,json:async()=>({})}} assert(url in samples,url);return {ok:true,json:async()=>samples[url]}};
const context={document,fetch,window:{scrollTo(){}},console,Date,Number,Error,Promise,encodeURIComponent,setTimeout};
vm.createContext(context);vm.runInContext(js,context);
const tick=()=>new Promise(resolve=>setTimeout(resolve,0));
async function main(){
 await tick();await tick();
 assert(elements.systemStatus.children.length,'system status rendered');
 vm.runInContext("state = {...state,paypal:true}; connectors = [{id:'paypal',role:'Payments',provider:'PayPal Orders API',status:'verified',detail:'Verified'}]; renderSystem(); renderConnectors();",context);
 assert.equal(elements.connectorPayPal.textContent,'Verified','overview connector status reflects verification');
 vm.runInContext("actionRequests = [{id:'workflow-action',action:'refund_payment',resourceId:'order-123456',status:'PENDING_APPROVAL'}]; renderOrders();",context);
 assert.equal(vm.runInContext("orderWorkflowStatus('order-123456')",context),'Approval needed','orders show provider action approvals');
 vm.runInContext("actionRequests = [{id:'workflow-action',action:'refund_payment',resourceId:'order-failed',status:'FAILED'}];",context);
 assert.equal(vm.runInContext("orderWorkflowStatus('order-failed')",context),'Action failed','orders show failed provider actions');
 assert(elements.recentOrders.children.length,'recent orders rendered');
 assert(elements.recentAudit.children.length,'audit rendered');
 assert(elements.downloadAuditExport && elements.downloadAuditAnchor,'audit archive controls rendered');
 await elements.verifyAudit.click();assert(elements.auditVerificationStatus.textContent.includes('1 events checked'),'audit chain verification result rendered');
 assert(elements.approvalsList.children.length,'approval rendered'); assert(elements.tasksList.children.length,'follow-up task rendered');
 nav.find(e=>e.dataset.view==='orders').click(); assert.equal(elements['view-orders'].hidden,false); assert.equal(elements['view-overview'].hidden,true);
 const table=elements.ordersList.children[0].children[0];const row=table.children[1].children[0];row.children[6].children[0].click();
 assert.equal(elements['view-detail'].hidden,false);assert(elements.orderDetail.children.length,'order detail rendered');
 const refundCard=elements.orderDetail.children[1].children[0].children[2];refundCard.children[2].click();
 assert.equal(elements.refundRequestDialog.open,true,'refund request dialog opens for a captured order');
 await elements.refundRequestForm.listeners.submit({preventDefault(){}});assert.equal(elements.refundRequestError.hidden,false,'refund reason is required');
 elements.refundReason.value='Customer requested a Sandbox refund';await elements.refundRequestForm.listeners.submit({preventDefault(){}});
 assert.equal(lastPost.url,'/api/actions/refund_payment');assert.deepEqual(JSON.parse(lastPost.options.body),{resourceId:'order-123456',amount:49,reason:'Customer requested a Sandbox refund'});assert.equal(elements.refundRequestDialog.open,false);
 vm.runInContext("actionRequests = [{id:'completed-refund',action:'refund_payment',resourceId:'order-123456',status:'COMPLETED',amount:20,result:'Refund completed.',providerReference:'REFUND-1',reconciliationReason:'Provider status confirmed'}]; renderDetail(); openRefundRequest(orders[0]);",context);
 assert.equal(elements.orderDetail.children[1].children[1].children.length,3,'order details show provider action results');
 assert.equal(elements.refundAmount.value,'29.00','refund dialog uses the remaining balance');assert.equal(elements.refundAmount.max,'29.00');elements.refundRequestDialog.close();
 nav.find(e=>e.dataset.view==='approvals').click();
 vm.runInContext("actionRequests = [{id:'action-123',action:'refund_payment',resourceId:'order-123456',status:'PENDING_APPROVAL',policyVersion:1,reason:'Customer requested a Sandbox refund',amount:49}]; renderApprovals();",context);
 const providerAction=elements.approvalsList.children[1];providerAction.children[2].children[0].click();
 assert.equal(elements.actionApprovalDialog.open,true,'provider action approval dialog opens');lastPost=undefined;await elements.approveProviderAction.click();
 assert.equal(elements.actionApprovalError.hidden,false,'provider action decision reason is required');assert.equal(lastPost,undefined,'missing provider action reason must not send decision');
 elements.actionDecisionReason.value='Reviewed the Sandbox refund request';await elements.approveProviderAction.click();await tick();
 assert.equal(lastPost.url,'/api/actions/action-123/decision');assert.deepEqual(JSON.parse(lastPost.options.body),{decision:'approve',reason:'Reviewed the Sandbox refund request'});assert.equal(elements.actionApprovalDialog.open,false);
 const approval=elements.approvalsList.children[0];approval.children[2].children[0].click();
 assert.equal(elements.approvalDialog.open,true); assert.equal(elements.dialogPaymentStatus.textContent,'Captured in PayPal sandbox');
 lastPost=undefined;await elements.approve.click();assert.equal(elements.dialogError.hidden,false);assert.equal(lastPost,undefined,'missing reason must not send decision');
 elements.decisionReason.value='Reviewed the proposal';
 await elements.approve.click();await tick();
 assert.equal(lastPost.url,'/api/proposals/proposal-123/decision');
 const sent=JSON.parse(lastPost.options.body);assert.deepEqual(sent,{decision:'approve',reason:'Reviewed the proposal'});
 assert.equal(lastPost.options.headers['X-CSRF-Token'],'test-csrf-token');
 assert.equal(elements.approvalDialog.open,false);
 vm.runInContext("roles = [{id:'r-child',name:'Child role',kind:'agent',permissions:['read_orders','propose_followup']}]; agents = [{id:'a-parent',name:'Parent',roleId:'r-child',parentId:null,effectivePermissions:['read_orders']}]; renderIdentities();",context);
 elements.agentRole.value='r-child'; elements.agentParent.value='a-parent';
 vm.runInContext("renderPermissionPreview()",context);
 assert(elements.permissionPreview.children[0].textContent.includes('read orders'));
 assert(!elements.permissionPreview.children[0].textContent.includes('propose followup'),'preview must respect parent permissions');
 assert(elements.rolesList.children.length && elements.agentsList.children.length,'role and agent definitions rendered'); elements.orderSearch.value='no matching order'; elements.orderSearch.listeners.input(); assert(elements.ordersList.children[0].textContent.includes('No orders match')); elements.productSearch.value='missing'; elements.productSearch.listeners.input(); assert(elements.products.children[0].textContent.includes('No products match')); elements.approvalResolvedTab.click(); assert(elements.approvalsList.children[0].textContent.includes('No resolved actions')); elements.approvalPendingTab.click(); assert.equal(elements.approvalsList.children[0].tag,'article');

 vm.runInContext("followupPolicy = {mode:'approval_required',version:2}; openApproval({id:'old',orderId:'order-123456',reason:'Old action',policyVersion:1});",context);
 assert.equal(elements.approve.disabled,true,'stale approval must be disabled');
 assert(elements.dialogPolicy.textContent.includes('new review'),'stale policy must be explained');
 elements.approvalDialog.close();
 samples['/api/auth/session'].authenticated=false;
 await vm.runInContext("startSession()",context);
 assert.equal(elements.loginDialog.open,true,'signed-out user is prompted to sign in');
 elements.loginUsername.value='operator';elements.loginPassword.value='temporary-test-password';
 await elements.loginForm.listeners.submit({preventDefault(){}});
 assert.equal(elements.loginDialog.open,false,'successful login closes the sign-in dialog');
 assert.equal(elements.loginPassword.value,'','password is cleared after login');
 await elements.logoutButton.click();
 assert.equal(elements.loginDialog.open,true,'logout returns to sign-in');
 console.log('PASS login, navigation, policy decisions and delegated permissions');
}
main().catch(e=>{console.error(e);process.exitCode=1});











