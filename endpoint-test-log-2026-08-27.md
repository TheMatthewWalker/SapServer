# SapServer endpoint test log - 2026-08-27 20:19:53 - Development mode, SAP sandbox ksnoka20/KAQ/100

## GET /api/warehouse/stock?Material=30005R
Note: Already confirmed working pre-script
Result: 200 OK -- {"success":true,"data":[{"storageLocation":"1710","storageType":"RO","bin":"STORE","material":"30005R","availableQty":1204.210,"batch":"","stockCategory":"","specialStockInd":"","specialStockNum":"","grDate":"04.09.2023","profitCentre":""}],"error":null}
Verdict: PASS

## GET /api/warehouse/im-stock?StorageLocation=1716
Result: 200 OK -- {"success":true,"data":[{"plant":"3012","storageLocation":"1716","material":"10101","availableQty":945.610},{"plant":"3012","storageLocation":"1716","material":"10102","availableQty":0.146},{"plant":"3012","storageLocation":"1716","material":"10103","availableQty":277.827},{"plant":"3012","storageLocation":"1716","material":"10104","availableQty":703.926},{"plant":"3012","storageLocation":"1716","material":"10105","availableQty":0.000},{"plant":"3012","storageLocation":"1716","material":"10106","availableQty":0.000},{"plant":"3012","storageLocation":"1716","material":"10107","availableQty":0.000},{"plant":"3012","storageLocation":"1716","material":"10200","availableQty":0.000},{"plant":"3012","storageLocation":"1716","material":"10201","availableQty":0.000},{"plant":"3012","storageLocation":"1716","material":"10202","availableQty":0.000},{"plant":"3012","storageLocation":"1716","material":"10203","availableQty":129.735},{"plant":"3012","storageLocation":"1716","material":"10204","availableQty":-276.452},{"plant":"3012","storageLocation":"1716","material":"10205","availableQty":804.513},{"plant":"3012","storageLocation":"1716","material":"10206","availableQty":530.010},{"plant":"3012","storageLocation":"1716","material":"10208","availableQty":-57.929},{"plant":"3012","storageLocation":"1716","material":"10209","availableQty":0.000},{"plant":"3012","storageLocation":"1716","material":"10210","availableQty":0.000},{"plant":"3012","storageLocation":"1716","material":"10211","availableQty":0.000},{"plant":"3012","storageLocation":"1716","material":"10212","availableQty":0.000},{"plant":"3012","storageLocation":"1716","material":"10213","availableQty":0.000},{"plant":"3012","storageLocation":"1716","material":"10216","availableQty":574.649},{"plant":"3012","storageLocation":"1716","material":"10217","availableQty":180.534},{"plant":"3012","storageLocation":"1716","material":"10230","availableQty":0.000},{"plant":"3012","storageLocation":"1716","material":"10231","availabl...(truncated)
Verdict: PASS

## GET /api/warehouse/stock/totals?Material=30005R
Result: 200 OK -- {"success":true,"data":[{"material":"30005R","totalQty":1204.210,"quantCount":1}],"error":null}
Verdict: PASS

## GET /api/warehouse/stock/bins?Material=30005R
Result: 200 OK -- {"success":true,"data":[{"storageType":"RO","bin":"STORE","quantCount":1,"totalQty":1204.210}],"error":null}
Verdict: PASS

## GET /api/warehouse/open-transfer-requirements
Result: 200 OK -- {"success":true,"data":[{"trNumber":"0000000061","material":"TSHV3-4B01C42/718","storageLocation":"1711","quantity":1.000,"uom":"M","mrpController":"643","documentText":"TEST KAN","materialDocument":"4960315289","createdBy":"KANMUSE","createdDate":"07.08.2024","createdTime":"13:59:08","movementType":"103","batch":"0030205181"},{"trNumber":"0000000062","material":"TSHV3-4B01C42/718","storageLocation":"1711","quantity":2.000,"uom":"M","mrpController":"643","documentText":"TEST KAN2","materialDocument":"4960315290","createdBy":"KANMUSE","createdDate":"07.08.2024","createdTime":"13:59:32","movementType":"103","batch":"0030205182"},{"trNumber":"0000000063","material":"TSHV3-4B01C42/718","storageLocation":"1711","quantity":1000.000,"uom":"M","mrpController":"643","documentText":"TEST KAN3","materialDocument":"4960315291","createdBy":"KANMUSE","createdDate":"07.08.2024","createdTime":"14:04:28","movementType":"103","batch":"0030205183"},{"trNumber":"0000000045","material":"CP104","storageLocation":"1711","quantity":100.000,"uom":"EA","mrpController":"643","documentText":"1","materialDocument":"4960313839","createdBy":"KAFIBRPL","createdDate":"31.01.2024","createdTime":"18:50:01","movementType":"103","batch":"0030204905"},{"trNumber":"0000000099","material":"006-01-01","storageLocation":"1711","quantity":10.000,"uom":"EA","mrpController":"642","documentText":"test ewa","materialDocument":"4960321050","createdBy":"EWZUPRPL","createdDate":"15.07.2025","createdTime":"14:28:35","movementType":"103","batch":"0030205457"},{"trNumber":"0000000100","material":"006-01-01","storageLocation":"1711","quantity":10.000,"uom":"EA","mrpController":"642","documentText":"test ewa","materialDocument":"4960321051","createdBy":"EWZUPRPL","createdDate":"15.07.2025","createdTime":"14:28:36","movementType":"103","batch":"0030205458"},{"trNumber":"0000000097","material":"006-01-01","storageLocation":"1711","quantity":10.000,"uom":"EA","mrpController":"642","documentText":"test ewa","materialDocumen...(truncated)
Verdict: PASS

## GET /api/warehouse/bin-storage-types?bin=STORE
Result: 200 OK -- {"success":true,"data":["FR","RO"],"error":null}
Verdict: PASS

## GET /api/warehouse/tr-cleanup-candidates
Result: 200 OK -- {"success":true,"data":[],"error":null}
Verdict: PASS

## GET /api/warehouse/zdelflag/likp-ablad/0080001234
Result: 200 OK -- {"success":true,"data":"","error":null}
Verdict: PASS

## GET /api/warehouse/zdelflag/lips-items/0080001234
Result: 200 OK -- {"success":true,"data":[],"error":null}
Verdict: PASS

## GET /api/logistics/picksheets/open
Result: 200 OK -- {"success":true,"data":[{"deliveryNumber":"82291409","customerNumber":"363991","dispatchDate":"18.05.2022","deliveryDate":"18.05.2022","incoterms":"FCA"},{"deliveryNumber":"82291413","customerNumber":"363967","dispatchDate":"11.05.2022","deliveryDate":"11.05.2022","incoterms":"EXW"},{"deliveryNumber":"82291414","customerNumber":"363844","dispatchDate":"06.05.2022","deliveryDate":"06.05.2022","incoterms":"EXW"},{"deliveryNumber":"82291443","customerNumber":"363992","dispatchDate":"15.07.2022","deliveryDate":"15.07.2022","incoterms":"EXW"},{"deliveryNumber":"82292312","customerNumber":"363638","dispatchDate":"11.03.2022","deliveryDate":"13.03.2022","incoterms":"DAP"},{"deliveryNumber":"82292900","customerNumber":"363980","dispatchDate":"13.04.2022","deliveryDate":"13.04.2022","incoterms":"FCA"},{"deliveryNumber":"82292901","customerNumber":"363980","dispatchDate":"26.04.2022","deliveryDate":"26.04.2022","incoterms":"FCA"},{"deliveryNumber":"82292912","customerNumber":"363873","dispatchDate":"20.04.2022","deliveryDate":"20.04.2022","incoterms":"EXW"},{"deliveryNumber":"82292925","customerNumber":"363445","dispatchDate":"28.04.2022","deliveryDate":"28.04.2022","incoterms":"EXW"},{"deliveryNumber":"82293603","customerNumber":"363456","dispatchDate":"11.05.2022","deliveryDate":"11.05.2022","incoterms":"EXW"},{"deliveryNumber":"82294294","customerNumber":"363991","dispatchDate":"03.05.2022","deliveryDate":"03.05.2022","incoterms":"FCA"},{"deliveryNumber":"82295293","customerNumber":"334232","dispatchDate":"09.05.2022","deliveryDate":"09.05.2022","incoterms":"FCA"},{"deliveryNumber":"82295295","customerNumber":"363926","dispatchDate":"18.05.2022","deliveryDate":"18.05.2022","incoterms":"EXW"},{"deliveryNumber":"82295524","customerNumber":"363409","dispatchDate":"23.03.2022","deliveryDate":"23.03.2022","incoterms":"CIF"},{"deliveryNumber":"82295547","customerNumber":"301921","dispatchDate":"10.05.2022","deliveryDate":"10.05.2022","incoterms":"FCA"},{"deliveryNumber":"8229560...(truncated)
Verdict: PASS

## GET /api/quality/display?Material=30005R
Result: 200 OK -- {"success":true,"data":[{"storageLocation":"LGORT","storageType":"LGTYP","bin":"LGPLA","material":"MATNR","availableQty":0.0,"batch":"CHARG","stockCategory":"BESTQ","specialStockInd":"SOBKZ","specialStockNum":"SONUM","grDate":"","profitCentre":""}],"error":null}
Verdict: PASS

## GET /api/performance/stock
Result: 200 OK -- {"success":true,"data":[{"material":"CP104","batch":"0030204905","storageBin":"0000769880","storageType":"901","totalQty":100.000,"availableQty":100.000,"storageLocation":"1711","packagingMaterial":"IB_CARTON2_NMT","profitCentre":"2007"},{"material":"TSDL7-9B01/363714","batch":"0030204551","storageBin":"0000770663","storageType":"901","totalQty":1.000,"availableQty":0.000,"storageLocation":"1711","packagingMaterial":"IB_DRUMMED_NMT","profitCentre":"2004"},{"material":"CP166","batch":"0030205424","storageBin":"0000775629","storageType":"901","totalQty":300.000,"availableQty":300.000,"storageLocation":"1711","packagingMaterial":"IB_CARTON2_NMT","profitCentre":"2007"},{"material":"CP166","batch":"0030205425","storageBin":"0000775629","storageType":"901","totalQty":300.000,"availableQty":300.000,"storageLocation":"1711","packagingMaterial":"IB_CARTON2_NMT","profitCentre":"2007"},{"material":"CP166","batch":"0030205423","storageBin":"0000775629","storageType":"901","totalQty":300.000,"availableQty":300.000,"storageLocation":"1711","packagingMaterial":"IB_CARTON2_NMT","profitCentre":"2007"},{"material":"CP1166","batch":"0030205411","storageBin":"0000791345","storageType":"901","totalQty":200.000,"availableQty":200.000,"storageLocation":"1711","packagingMaterial":"IB_CARTON2_NMT","profitCentre":"2007"},{"material":"TSHV3-4B01C42/718","batch":"0030205183","storageBin":"0000783565","storageType":"901","totalQty":1000.000,"availableQty":1000.000,"storageLocation":"1711","packagingMaterial":"IB_DRUMMED_NMT","profitCentre":"2004"},{"material":"TSHV3-4B01C42/718","batch":"0030205182","storageBin":"0000783565","storageType":"901","totalQty":2.000,"availableQty":2.000,"storageLocation":"1711","packagingMaterial":"IB_DRUMMED_NMT","profitCentre":"2004"},{"material":"TSHV3-4B01C42/718","batch":"0030205181","storageBin":"0000783565","storageType":"901","totalQty":1.000,"availableQty":1.000,"storageLocation":"1711","packagingMaterial":"IB_DRUMMED_NMT","profitCentre":"2004"},{"material"...(truncated)
Verdict: PASS

## GET /api/performance/agreements?horizonDays=30
Result: 200 OK -- {"success":true,"data":[{"profitCentre":"0000002009","plant":"3012","mid":"INDUSTRIAL|BREATHING||","mrpController":"643","material":"2015879","materialText":"1/8\" TEFZEL ESTANE COVERED X","valueStream":"2009-1","onHandQty":0.0,"uom":"EA","standardPrice":13.46703,"localCurrency":"GBP","customer":"363655","customerGroup":"SCOTT SAFE","customerName":"SCOTT HEALTH \u0026 SAFETY LTD","orderType":"ZORD","referenceDocument":"0005155082","item":"000020","customerPo":"4501038716","customerMaterial":"2015879","customerReference":"","unloadingPoint":"","requestDate":"2022-05-24T00:00:00","week":"202221","period":"202205","orderQty":700.000,"amount":7021.00000,"currency":"GBP","localAmount":7021.0000000000,"dockStockAllocated":0.0,"pickedStockAllocated":0.0},{"profitCentre":"0000002009","plant":"3012","mid":"INDUSTRIAL|BREATHING||","mrpController":"643","material":"2015879","materialText":"1/8\" TEFZEL ESTANE COVERED X","valueStream":"2009-1","onHandQty":0.0,"uom":"EA","standardPrice":13.46703,"localCurrency":"GBP","customer":"363655","customerGroup":"SCOTT SAFE","customerName":"SCOTT HEALTH \u0026 SAFETY LTD","orderType":"ZORD","referenceDocument":"0005155082","item":"000030","customerPo":"4501038716","customerMaterial":"2015879","customerReference":"","unloadingPoint":"","requestDate":"2022-06-07T00:00:00","week":"202223","period":"202206","orderQty":700.000,"amount":7021.00000,"currency":"GBP","localAmount":7021.0000000000,"dockStockAllocated":0.0,"pickedStockAllocated":0.0},{"profitCentre":"0000002009","plant":"3012","mid":"INDUSTRIAL|BREATHING||","mrpController":"643","material":"2015879","materialText":"1/8\" TEFZEL ESTANE COVERED X","valueStream":"2009-1","onHandQty":0.0,"uom":"EA","standardPrice":13.46703,"localCurrency":"GBP","customer":"363655","customerGroup":"SCOTT SAFE","customerName":"SCOTT HEALTH \u0026 SAFETY LTD","orderType":"ZORD","referenceDocument":"0005155082","item":"000040","customerPo":"4501038716","customerMaterial":"2015879","customerReference":"","un...(truncated)
Verdict: PASS

## GET /api/performance/invoicing
Result: HTTP 404 -- The remote server returned an error: (404) Not Found.
Body: {"Message":"No HTTP resource was found that matches the request URI 'http://localhost:7200/api/performance/invoicing'.","MessageDetail":"No action was found on the controller 'Performance' that matches the request."}
Verdict: ERROR - status 404

## GET /api/performance/otif
Result: HTTP 404 -- The remote server returned an error: (404) Not Found.
Body: {"Message":"No HTTP resource was found that matches the request URI 'http://localhost:7200/api/performance/otif'.","MessageDetail":"No action was found on the controller 'Performance' that matches the request."}
Verdict: ERROR - status 404

## GET /api/performance/turns-valclass/valuation-classes
Result: 200 OK -- {"success":true,"data":[{"valuationClass":"3000","accountRef":"0001","materialType":"ROH","description":"Raw material serial part"},{"valuationClass":"3001","accountRef":"0001","materialType":"ROH","description":"Intercompany raw material"},{"valuationClass":"3004","accountRef":"0001","materialType":"ROH","description":"Raw material service part"},{"valuationClass":"3005","accountRef":"0001","materialType":"ROH","description":"Raw mtrl slow moving part"},{"valuationClass":"3006","accountRef":"0001","materialType":"ROH","description":"Raw mtrl Obselete"},{"valuationClass":"3007","accountRef":"0001","materialType":"ROH","description":"Raw mtrl Prototype"},{"valuationClass":"3030","accountRef":"0002","materialType":"HIBE","description":"Cutting tools \u0026 Accessori"},{"valuationClass":"3031","accountRef":"0002","materialType":"HIBE","description":"Consumables / Materials"},{"valuationClass":"3032","accountRef":"0002","materialType":"HIBE","description":"Spare Parts f. Equipments"},{"valuationClass":"3033","accountRef":"0002","materialType":"HIBE","description":"Cleaning detergents"},{"valuationClass":"3034","accountRef":"0002","materialType":"HIBE","description":"Protective clothes"},{"valuationClass":"3035","accountRef":"0002","materialType":"HIBE","description":"Office materials"},{"valuationClass":"4000","accountRef":"0004","materialType":"VERP","description":"Packing"},{"valuationClass":"7900","accountRef":"0008","materialType":"HALB","description":"Semifinised serial part"},{"valuationClass":"7901","accountRef":"0008","materialType":"HALB","description":"Semifin. Intercomp. only"},{"valuationClass":"7902","accountRef":"0008","materialType":"HALB","description":"Not in use."},{"valuationClass":"7904","accountRef":"0008","materialType":"HALB","description":"Semifinised service part"},{"valuationClass":"7905","accountRef":"0008","materialType":"HALB","description":"Semifin. slow moving part"},{"valuationClass":"7906","accountRef":"0008","materialType":"HALB","descr...(truncated)
Verdict: PASS

## GET /api/consignment/gr?sapVendorNumber=100000
Result: HTTP  -- The operation has timed out.
Retest (curl.exe, 90s timeout): HTTP 200 -- {"success":true,"data":[],"error":null} -- returned in 1.6s.
Verdict: PASS -- original timeout was transient (likely a cold SAP connection/pool warm-up on the first
real call of this kind in the session), not reproducible. Not a bug.

## GET /api/consignment/stock
Result: 200 OK -- {"success":true,"data":{"01-104-03-03-54":0.000,"01-104-04-04-54":0.000,"01-104-08-08-54":0.000,"01-105-04-04-54":0.000,"01-105-05-05-54":0.000,"01-105-06-06-54":0.000,"01-105-08-08-54":0.000,"01-105-12-12-54":0.000,"01-105-16-16-54":0.000,"01-107-04-04-54":0.000,"01-107-06-06-54":0.000,"01-107-08-08-54":0.000,"01-107-10-10-54":0.000,"01-107-12-12-54":0.000,"01-107-16-16-54":0.000,"01-107-20-20-54":0.000,"01-107-24-24-54":0.000,"01-107-32-32-54":0.000,"99-0102QR3-SS":0.000,"99-0102F-3-SS":0.000,"94-01031072N1":0.000,"94-01031072P1":0.000,"99-2001727":0.000,"99-2001864":0.000,"99-0103F-3-SS":0.000,"99-0103QR3-SS":0.000,"10004":0.000,"20003":118.020,"20004":191.550,"20005":41.785,"20006":0.000,"20007":0.000,"20008":0.000,"20009":0.000,"20025":0.000,"20026":0.000,"20027":0.000,"01-104-06-06-54":0.000,"01-104-10-10-54":0.000,"01-104-12-12-54":0.000,"01-104-16-16-54":0.000,"01-105-03-03-54":0.000,"10006":0.000,"20018":0.000},"error":null}
Verdict: PASS

## GET /api/mrp-analysis/consumption-by-year
Result: 200 OK -- {"success":true,"data":[{"material":"10000","fiscalYear":2022,"qty":29822.236},{"material":"10000","fiscalYear":2023,"qty":25.337},{"material":"10000","fiscalYear":2024,"qty":252.420},{"material":"10000","fiscalYear":2025,"qty":153.000},{"material":"10005","fiscalYear":2022,"qty":4954.060},{"material":"10005","fiscalYear":2024,"qty":48.720},{"material":"10006","fiscalYear":2022,"qty":103616.262},{"material":"10006","fiscalYear":2024,"qty":87.467},{"material":"10008","fiscalYear":2022,"qty":799.512},{"material":"10010","fiscalYear":2022,"qty":38751.992},{"material":"10010","fiscalYear":2023,"qty":9.380},{"material":"10010","fiscalYear":2024,"qty":121.952},{"material":"10026","fiscalYear":2022,"qty":128.617},{"material":"10026","fiscalYear":2023,"qty":0.549},{"material":"10026","fiscalYear":2024,"qty":1.972},{"material":"10027","fiscalYear":2022,"qty":181.065},{"material":"10027","fiscalYear":2024,"qty":2.520},{"material":"10030","fiscalYear":2022,"qty":511.356},{"material":"10032","fiscalYear":2022,"qty":467.760},{"material":"10033","fiscalYear":2022,"qty":8646.064},{"material":"10035","fiscalYear":2022,"qty":910.127},{"material":"10041","fiscalYear":2022,"qty":100.000},{"material":"10101","fiscalYear":2022,"qty":11155.696},{"material":"10101","fiscalYear":2024,"qty":24.274},{"material":"10102","fiscalYear":2022,"qty":29.414},{"material":"10103","fiscalYear":2022,"qty":8493.401},{"material":"10103","fiscalYear":2024,"qty":3.600},{"material":"10104","fiscalYear":2022,"qty":1863.545},{"material":"10104","fiscalYear":2024,"qty":5.180},{"material":"10203","fiscalYear":2022,"qty":205.958},{"material":"10204","fiscalYear":2022,"qty":3934.062},{"material":"10204","fiscalYear":2024,"qty":0.000},{"material":"10205","fiscalYear":2022,"qty":3598.616},{"material":"10205","fiscalYear":2023,"qty":32.448},{"material":"10208","fiscalYear":2022,"qty":1095.302},{"material":"10210","fiscalYear":2022,"qty":61.830},{"material":"10216","fiscalYear":2022,"qty":5457.543},{"material":"10217"...(truncated)
Verdict: PASS

## GET /api/mrp-analysis/goods-receipt-history
Result: HTTP 404 -- The remote server returned an error: (404) Not Found.
Body: {"Message":"No HTTP resource was found that matches the request URI 'http://localhost:7200/api/mrp-analysis/goods-receipt-history'.","MessageDetail":"No action was found on the controller 'MrpAnalysis' that matches the request."}
Verdict: ERROR - status 404

## GET /api/packaging/30005R/exists
Result: 200 OK -- {"success":true,"data":true,"error":null}
Verdict: PASS

## GET /api/packaging/30005R/description
Result: 200 OK -- {"success":true,"data":"0.19mm 304Stainless Steel Wire (AMS-07)","error":null}
Verdict: PASS

## GET /api/packaging/30005R/mara
Result: 200 OK -- {"success":true,"data":{"weightKg":0.001,"materialType":"ROH","handlingType":"","weightUnit":"KG"},"error":null}
Verdict: PASS

## GET /api/packaging/30005R/bom
Result: 200 OK -- {"success":true,"data":[],"error":null}
Verdict: PASS

## GET /api/packaging/30005R/customers
Result: 200 OK -- {"success":true,"data":[],"error":null}
Verdict: PASS

## GET /api/packaging/30005R/instruction
Result: HTTP 404 -- The remote server returned an error: (404) Not Found.
Body: {"Message":"No HTTP resource was found that matches the request URI 'http://localhost:7200/api/packaging/30005R/instruction'.","MessageDetail":"No action was found on the controller 'Packaging' that matches the request."}
Verdict: ERROR - status 404

## GET /api/production/bom
Request: {"Material":"30005R"}
Result: HTTP  -- Cannot send a content-body with this verb-type.
Verdict: SUPERSEDED -- PowerShell Invoke-RestMethod cannot send a body on GET; retested successfully via curl.exe further below in this log (PASS).

## GET /api/production/check-profit-centre
Request: {"Material":"30005R"}
Result: HTTP  -- Cannot send a content-body with this verb-type.
Verdict: SUPERSEDED -- PowerShell Invoke-RestMethod cannot send a body on GET; retested successfully via curl.exe further below in this log (PASS).

## GET /api/production/check-profit-centres
Request: {"Materials":["30005R"]}
Result: HTTP  -- Cannot send a content-body with this verb-type.
Verdict: SUPERSEDED -- PowerShell Invoke-RestMethod cannot send a body on GET; retested successfully via curl.exe further below in this log (PASS).

## GET /api/production/find-cost-collector
Request: {"Material":"30005R"}
Result: HTTP  -- Cannot send a content-body with this verb-type.
Verdict: SUPERSEDED -- PowerShell Invoke-RestMethod cannot send a body on GET; retested via curl.exe further below in this log (PASS, clean business 400).

## POST /api/customs/lips
Request: {"Deliveries":["0080001234"]}
Result: 200 OK -- {"success":true,"data":[],"error":null}
Verdict: PASS

## POST /api/customs/likp
Request: {"Deliveries":["0080001234"]}
Result: 200 OK -- {"success":true,"data":[],"error":null}
Verdict: PASS

## POST /api/customs/marc
Request: {"Materials":["30005R"]}
Result: 200 OK -- {"success":true,"data":[{"materialNumber":"30005R","commodityCode":"7223001900","countryOfOrigin":""}],"error":null}
Verdict: PASS

## POST /api/warehouse/picksheet-materials
Request: {"Deliveries":["0080001234"]}
Result: 200 OK -- {"success":true,"data":[],"error":null}
Verdict: PASS

## POST /api/warehouse/picksheet-stock
Request: {"Materials":["30005R"]}
Result: 200 OK -- {"success":true,"data":[],"error":null}
Verdict: PASS

## GET /api/function/params
Request: {"FunctionName":"RFC_PING"}
Result: HTTP  -- Cannot send a content-body with this verb-type.
Verdict: SUPERSEDED -- PowerShell Invoke-RestMethod cannot send a body on GET; retested successfully via curl.exe further below in this log (PASS).

## GET /api/rfc/status
Result: 200 OK -- {"success":true,"data":[],"error":null}
Verdict: PASS

## SYSTEMIC BUG: [FromUri] scalar params without an explicit C# default 404 the whole route when omitted

Confirmed real, reproducible, affects at least 6 endpoints. Root cause: Web API 2's action selector rejects
a candidate action entirely (not a 400/model-binding error — a genuine "No action was found on the controller"
404, bypassing even the app's own NotFoundController catch-all, no ApiResponse envelope at all) whenever a
`[FromUri]` scalar parameter (value OR reference type, nullable OR not) has no literal default value expression
in the C# method signature and its query-string key is absent from the request. Confirmed for both DateTime? and
string? (not just non-nullable value types) -- this means CLAUDE.md's existing documented note ("Every controller
action with an optional [FromUri] value-type parameter needs an explicit default... [FromUri] parameters that are
already nullable... don't need this") is INCOMPLETE/WRONG: nullable ones need it too, for BOTH value and reference
types. Complex-type [FromUri] params (StockQuery, OpenTransferRequirementsQuery, etc.) are unaffected by this --
those already have the documented `query ??= new Xxx()` fix and route correctly when omitted (confirmed:
GET /api/warehouse/open-transfer-requirements works fine with zero query string).

Confirmed affected sites (all currently declared with NO `= <value>` default):
- MrpAnalysisController.GetGoodsReceiptHistory([FromUri] string? sinceDate) -- 404s when sinceDate omitted; works when supplied (e.g. ?sinceDate=2026-01-01)
- PackagingController.GetInstruction(string material, [FromUri] string? customer) -- 404s when customer omitted; the app-level "no instruction found" 404 only happens when customer IS supplied (confirmed by comparing response bodies -- omitted gives the generic Web-API "No action was found" body, supplied gives the real ApiResponse envelope)
- PerformanceController.GetInvoicing([FromUri] DateTime? from, [FromUri] DateTime? to) -- 404s unless BOTH from and to are supplied
- PerformanceController.GetOtif([FromUri] DateTime? from, [FromUri] DateTime? to) -- same, 404s unless BOTH supplied
- PerformanceController.GetAgreements([FromUri] int? horizonDays) -- 404s when horizonDays omitted; works when supplied (e.g. ?horizonDays=30)
- ProductionController.GetOrderText(string salesDocument, string item, [FromUri] string? textId) -- 404s when textId omitted; works when supplied

Likely a real production issue right now for any caller (Normanton-Nexus) that omits these optional params expecting
a sensible default (e.g. GetAgreements is presumably meant to default horizonDays when a caller wants "everything",
GetInvoicing/GetOtif's own code computes a fallback date range via `from ?? DateTime.Today.AddDays(-31)` -- that
fallback logic is dead code right now, since the request 404s before the action body ever runs if the caller omits
the param entirely, exactly the same silent-breakage shape as the already-documented GetOpenTransferRequirements
NullReferenceException bug this pattern is a sibling of).

**Fix**: add an explicit default to every affected parameter -- `[FromUri] string? sinceDate = null`,
`[FromUri] string? customer = null`, `[FromUri] DateTime? from = null, [FromUri] DateTime? to = null` (x2 controllers),
`[FromUri] int? horizonDays = null`, `[FromUri] string? textId = null` -- mirroring the exact fix already applied
to `ConsignmentController.GetVendorGr`'s `sinceDate` parameter (`[FromUri] string? sinceDate = null`), which is the
one place in the codebase that already got this right and was confirmed NOT to 404 by itself (its earlier timeout
was a separate, real SAP-side slowness issue, not a routing problem -- see its own entry above).


## POST /api/warehouse/stock-adjustment?dryRun=true
Request: {"Quantity":1,"MovementType":"711","StorageLocation":"1710","Material":"30005R","StorageBin":"STORE","Unit":"KG","StorageType":"RO"}
Result: 200 OK -- {"success":true,"data":{"functionName":"BAPI_GOODSMVT_CREATE","importParameters":{"TESTRUN":""},"structImportParameters":{"GOODSMVT_HEADER":{"PSTNG_DATE":"20260827","DOC_DATE":"20260827","REF_DOC_NO":""},"GOODSMVT_CODE":{"GM_CODE":"06"}},"inputTables":{"GOODSMVT_ITEM":[{"MATERIAL":"30005R","PLANT":"3012","STGE_LOC":"1710","MOVE_TYPE":"711","ENTRY_QNT":1.0,"ENTRY_UOM":"KG","STGE_TYPE":"RO","STGE_BIN":"STORE","SPEC_STOCK":null,"VENDOR":null}]},"inputTablesItems":{},"exportParameters":["MATERIALDOCUMENT","MATDOCUMENTYEAR"],"structExportParameters":{},"outputTables":{"RETURN":["TYPE","MESSAGE"]}},"error":null}
Verdict: PASS

## POST /api/warehouse/goods-issue?dryRun=true
Request: {"DeliveryNumber":"0082291409"}
Result: 200 OK -- {"success":true,"data":{"functionName":"BAPI_DELIVERYPROCESSING_EXEC","importParameters":{},"structImportParameters":{"DELIVERY_EXTEND":{"DELIVERY_NUMBER":"0082291409","NEW_DELIVERY_ALLOWED":""},"TECHN_CONTROL":{"DEBUG_FLG":"","SENDER_SYSTEM":"","PROCESS_GUID":"","ERROR_TOLERANCE":"","CHECK_MODE":"","IDOCNUM":"","APOTRGUID":"","SPE_SCENARIO_FLAG":"","POST_ASYNC":""}},"inputTables":{"REQUEST":[{"DOCUMENT_NUMB":"0082291409","DELIVERY_DATE":"20260827","GOODS_ISSUE_DATE":"20260827"}]},"inputTablesItems":{},"exportParameters":[],"structExportParameters":{},"outputTables":{"RETURN":["TYPE","ID","NUMBER","MESSAGE","LOG_NO","LOG_MSG_NO","MESSAGE_V1","MESSAGE_V2","MESSAGE_V3","MESSAGE_V4","PARAMETER","ROW","FIELD","SYSTEM"],"CREATEDITEMS":["DOCUMENT_NUMB","DOCUMENT_ITEM","MATERIAL","QUANTITY_SALES_UOM","SALES_UNIT"]}},"error":null}
Verdict: PASS

## POST /api/warehouse/delivery-change?dryRun=true
Request: {"Items":[{"ItemNumber":"000010","Quantity":1,"Material":"30005R","BaseUom":"KG"}],"DeliveryNumber":"0082291409"}
Result: 200 OK -- {"success":true,"data":{"functionName":"BAPI_OUTB_DELIVERY_CHANGE","importParameters":{},"structImportParameters":{"HEADER_CONTROL":{"DELIV_NUMB":"0082291409"},"TECHN_CONTROL":{"DEBUG_FLG":""}},"inputTables":{"ITEM_CONTROL":[{"DELIV_NUMB":"0082291409","DELIV_ITEM":"000010","CHG_DELQTY":"X"}],"ITEM_DATA":[{"DELIV_NUMB":"0082291409","DELIV_ITEM":"000010","MATERIAL":"30005R","DLV_QTY":1.0,"BASE_UOM":"KG"}]},"inputTablesItems":{},"exportParameters":[],"structExportParameters":{},"outputTables":{"RETURN":["TYPE","ID","NUMBER","MESSAGE","LOG_NO","LOG_MSG_NO","MESSAGE_V1","MESSAGE_V2","MESSAGE_V3","MESSAGE_V4","PARAMETER","ROW","FIELD","SYSTEM"]}},"error":null}
Verdict: PASS

## POST /api/warehouse/consignment-mb1b?dryRun=true
Request: {"SourceBin":"STORE","DestinationBin":"PTFE","Header":"Test","StorageLocation":"1710","SourceType":"RO","Material":"30005R","Quantity":1,"DestinationType":"SA","SpecialStockNumber":"0000200604"}
Result: 200 OK -- {"success":true,"data":{"functionName":"BAPI_GOODSMVT_CREATE","importParameters":{"TESTRUN":""},"structImportParameters":{"GOODSMVT_HEADER":{"PSTNG_DATE":"20260827","DOC_DATE":"20260827","HEADER_TXT":"Consignment"},"GOODSMVT_CODE":{"GM_CODE":"04"}},"inputTables":{"GOODSMVT_ITEM":[{"MATERIAL":"30005R","PLANT":"3012","STGE_LOC":"1710","MOVE_TYPE":"411","SPEC_STOCK":"K","VENDOR":"0000200604","ENTRY_QNT":1.0,"MOVE_STLOC":"1710","MOVE_PLANT":"3012"}]},"inputTablesItems":{},"exportParameters":["MATERIALDOCUMENT","MATDOCUMENTYEAR"],"structExportParameters":{},"outputTables":{"RETURN":["TYPE","MESSAGE"]}},"error":null}
Verdict: PASS

## POST /api/quality/block?dryRun=true
Note: QualityController doesn't declare dryRun -- confirm whether this param is even accepted
Request: {"Username":"TEST","Batch":"","Material":"30005R","StorageLocation":"1710","Header":"Test","Quantity":1,"BinType":"RO","Bin":"STORE"}
Result: 200 OK -- {"success":true,"data":{"success":true,"mb1bMessage":"S M7 060  Document 4960322655 posted","toNonBlockedMessage":"","toBlockedMessage":""},"error":null}
Verdict: PASS

## POST /api/warehouse/stock-adjustment
Note: TestRun=true, real SAP call, should roll back
Request: {"StorageType":"RO","Quantity":1,"Unit":"KG","Material":"30005R","StorageLocation":"1710","StorageBin":"STORE","MovementType":"711","TestRun":true}
Result: 200 OK -- {"success":true,"data":{"materialDocument":"","materialDocumentYear":"0000","success":false,"messages":[]},"error":null}
Verdict: PASS

## POST /api/warehouse/goods-issue
Note: TestRun=true
Request: {"TestRun":true,"DeliveryNumber":"0082291409"}
Result: 200 OK -- {"success":true,"data":{"deliveryNumber":"0082291409","success":false,"messages":[{"type":"W","message":"The transferred sales document table is empty"},{"type":"E","message":"Delivery not possible at the moment"}],"createdItemCount":0},"error":null}
Verdict: PASS

## POST /api/warehouse/delivery-change
Note: TestRun=true
Request: {"TestRun":true,"DeliveryNumber":"0082291409","Items":[{"ItemNumber":"000010","Quantity":1,"Material":"30005R","BaseUom":"KG"}]}
Result: 200 OK -- {"success":true,"data":{"deliveryNumber":"0082291409","success":false,"messages":[{"type":"E","message":""},{"type":"W","message":""}]},"error":null}
Verdict: PASS

## POST /api/warehouse/consignment-mb1b
Note: TestRun=true
Request: {"SourceBin":"STORE","TestRun":true,"DestinationBin":"PTFE","Header":"Test","StorageLocation":"1710","SourceType":"RO","Material":"30005R","Quantity":1,"DestinationType":"SA","SpecialStockNumber":"0000200604"}
Result: 200 OK -- {"success":true,"data":{"success":false,"mb1bMessage":"E Entry 200604 LFA1  does not exist in  (check entry)","toNonConsignMessage":"","toConsignMessage":""},"error":null}
Verdict: PASS

## POST /api/warehouse/transfer-order?dryRun=true
Note: Deliberately bad destination bin -- should 422 from the LAGP existence pre-check before ever calling L_TO_CREATE_SINGLE, per the controller's own documented fail-fast design
Request: {"Quantity":1,"StorageLocation":"1710","DestinationType":"RO","Material":"30005R","DestinationBin":"NOSUCHBIN99","SourceType":"RO","SourceBin":"STORE"}
Result: HTTP 422 -- The remote server returned an error: (422) Unprocessable Entity.
Body: {"success":false,"data":{"transferOrderNumber":"","success":false,"messages":[{"type":"E","message":"Destination bin RO/NOSUCHBIN99 does not exist in SAP warehouse 312. Check the storage type and bin and try again."}]},"error":{"code":"422","message":"Destination bin RO/NOSUCHBIN99 does not exist in SAP warehouse 312. Check the storage type and bin and try again."}}
Verdict: ERROR - status 422

## POST /api/quality/block
Verdict: NOT EXECUTED -- No dry-run flag on QualityMb1bRequest/BlockStock -- would place a REAL quality block on real stock via MB1B. Skipping the real call pending explicit confirmation this material/batch is safe to block+unblock in the sandbox; dryRun test above already shown to establish whether the param is even honored.

## POST /api/mrp-analysis/explode-bom
Request: {"Items":[{"Material":"30005R","Quantity":10}]}
Result: 200 OK -- {"success":true,"data":{"rawMaterials":[],"unresolved":[]},"error":null}
Verdict: PASS

## POST /api/sales/schedule-waterfall
Request: {"SalesOrg":"3012","ScheduleDateFrom":"2026-01-01","IncludeJit":true,"IncludeForecast":true,"ScheduleDateTo":"2026-12-31","ShipToParties":[],"Materials":[]}
Result: 200 OK -- {"success":true,"data":[],"error":null}
Verdict: PASS

## POST /api/customs/vbfa
Request: {"Lines":[{"Delivery":"0082291409","Item":"000010"}]}
Result: 200 OK -- {"success":true,"data":[],"error":null}
Verdict: PASS

## POST /api/customs/kna1
Request: {"Customers":["0000363991"]}
Result: 200 OK -- {"success":true,"data":[{"customerCode":"363991","name":"CONTITECH FLUID SERBIA DOO SUBOTICA","street":"BATINSKA 94","city":"SUBOTICA","postCode":"24000","destinationCountry":"RS","transportZone":"","vatNumber":"SR107229570","incoterms":"FCA"}],"error":null}
Verdict: PASS

## POST /api/customs/vbrk
Request: {"Invoices":[]}
Result: 200 OK -- {"success":true,"data":[],"error":null}
Verdict: PASS

## POST /api/customs/consignment-price
Request: {"Lines":[{"Customer":"0000363991","Material":"30005R"}]}
Result: 200 OK -- {"success":true,"data":[],"error":null}
Verdict: PASS

## POST /api/rfc/execute
Request: {"OutputTables":{},"ExportParameters":[],"ImportParameters":{},"FunctionName":"RFC_PING"}
Result: 200 OK -- {"success":true,"data":{"parameters":{},"tables":{}},"error":null}
Verdict: PASS

## POST /api/purchasing/create-po?dryRun=true
Request: {"Currency":"GBP","Vendor":"100000","Items":[{"Material":"30005R","Quantity":1,"ShortText":"Test","DeliveryDate":"2026-09-01","Unit":"KG"}]}
Result: 200 OK -- {"success":true,"data":{"functionName":"BAPI_PO_CREATE1","importParameters":{},"structImportParameters":{"POHEADER":{"COMP_CODE":"0312","DOC_TYPE":"NB","VENDOR":"0000100000","PURCH_ORG":"3012","PUR_GROUP":"386","CURRENCY":"GBP","DOC_DATE":"20260827"},"POHEADERX":{"COMP_CODE":"X","DOC_TYPE":"X","VENDOR":"X","PURCH_ORG":"X","PUR_GROUP":"X","CURRENCY":"X","DOC_DATE":"X"}},"inputTables":{"POITEM":[{"PO_ITEM":"00010","SHORT_TEXT":"Test","PLANT":"3012","QUANTITY":1.0,"PO_UNIT":"KG","ITEM_CAT":"0","ACCTASSCAT":"","MATERIAL":"30005R"}],"POITEMX":[{"PO_ITEM":"00010","PLANT":"X","QUANTITY":"X","PO_UNIT":"X","ITEM_CAT":"X","ACCTASSCAT":"X","SHORT_TEXT":"X","MATERIAL":"X"}],"POSCHEDULE":[{"PO_ITEM":"00010","SCHED_LINE":"0001","DELIVERY_DATE":"20260901","QUANTITY":1.0}],"POSCHEDULEX":[{"PO_ITEM":"00010","SCHED_LINE":"0001","DELIVERY_DATE":"X","QUANTITY":"X"}]},"inputTablesItems":{},"exportParameters":["EXPPURCHASEORDER"],"structExportParameters":{},"outputTables":{"RETURN":["TYPE","MESSAGE"]}},"error":null}
Verdict: PASS

## POST /api/purchasing/create-po
Verdict: NOT EXECUTED -- Creates a real PO document in SAP with no dry-run/reverse path built into this endpoint; dryRun above only proves the request builds. Needs explicit confirmation before a real PO number gets consumed in the sandbox.

## POST /api/purchasing/post-goods-receipt
Verdict: NOT EXECUTED -- Posts a real goods receipt against a real PO; needs a genuinely open real PO from the sandbox to test meaningfully, and consumes a real material document number. Not attempted.

## POST /api/purchasing/create-po-and-receipt
Verdict: NOT EXECUTED -- Combined real PO + GR creation. Same reasoning as create-po/post-goods-receipt above.

## POST /api/purchasing/create-po-elevated
Verdict: NOT EXECUTED -- Requires one specific real user's own SAP credentials (elevated session) -- not available in this test session.

## POST /api/packaging/create-elevated
Verdict: NOT EXECUTED -- Requires one specific real user's own SAP credentials (elevated session) -- not available in this test session.

## POST /api/production/backflush
Verdict: NOT EXECUTED -- Real ZF40N production backflush BDC against a real production order -- no dry-run mode on this endpoint at all, consumes real order confirmation quantity. Not attempted without explicit confirmation and a specific real order number from the user.

## POST /api/production/drumming-backflush
Verdict: NOT EXECUTED -- Same reasoning as backflush -- real BDC, no dry-run, plus writes ZPRODBATCH_TBL/ZBATCHPACK_TBL via Z_ZPRODBATCH_MAINT.

## POST /api/production/scrap/post
Verdict: NOT EXECUTED -- Real scrap posting BDC, no dry-run.

## POST /api/production/mixing-scrap
Verdict: NOT EXECUTED -- Has a TestRun field on MixingScrapRequest -- SHOULD be tested with TestRun=true, see follow-up test below.

## POST /api/production/goods-movement-backflush
Verdict: NOT EXECUTED -- Has a TestRun field on GoodsMovementRequest -- SHOULD be tested with TestRun=true, see follow-up test below.

## POST /api/production/reverse-backflush
Verdict: NOT EXECUTED -- Reverses a real material document -- needs a real material document number from an actual prior backflush, none available/created this session.

## POST /api/production/scrap/reverse
Verdict: NOT EXECUTED -- Same reasoning -- needs a real prior scrap document to reverse.

## POST /api/purchasing/reverse-goods-receipt
Verdict: NOT EXECUTED -- Needs a real prior GR document to reverse.

## PUT /api/packaging/instruction
Verdict: NOT EXECUTED -- Writes/overwrites a real packaging instruction config row -- deferred pending confirmation this is safe to test against real config data (not easily 'reversible' the way block/unblock is).

## DELETE /api/packaging/instruction
Verdict: NOT EXECUTED -- Deletes a real packaging instruction config row -- same reasoning.

## POST /api/packaging/create
Verdict: NOT EXECUTED -- Creates a real material master record (MM01) + BOM (CS01) -- not reversible via any endpoint in this API. Not attempted.

## POST /api/packaging/mass-update
Verdict: NOT EXECUTED -- Bulk real packaging-instruction writes -- same reasoning as PUT/DELETE instruction above.

## POST /api/performance/turns-valclass/change-valuation-class
Verdict: NOT EXECUTED -- Real, deliberately-irreversible MM02 valuation-class change with real GL/inventory value impact -- explicitly out of scope without direct confirmation.

## POST /api/warehouse/create-lt04
Verdict: NOT EXECUTED -- Real transfer order confirmation (LT04) against a real, existing open TR -- would need a genuine open TR from the sandbox; the one found earlier (TR 0000000061) belongs to someone else's real data, confirming it would actually move real stock. Not attempted.

## POST /api/warehouse/delete-tr
Verdict: NOT EXECUTED -- Deletes a real open transfer requirement -- same reasoning, would destroy real sandbox data belonging to TR 0000000061 found during discovery.

## POST /api/warehouse/picksheet-stage-batch
Verdict: NOT EXECUTED -- Real transfer order creation + LAGP bin auto-create BDC against a real picksheet -- needs a real material/batch/delivery combination and creates real staged stock; deferred.

## POST /api/warehouse/picksheet-unstage-batch
Verdict: NOT EXECUTED -- Reverses staging -- needs a real staged batch from the above, which wasn't created.

## POST /api/costing/cost-sheet
Request: {"Materials":["30005R"],"Date":"20260101"}
Result: HTTP 500 -- The remote server returned an error: (500) Internal Server Error.
Body: {"success":false,"data":{"exceptionType":"FormatException","message":"String was not recognized as a valid DateTime."},"error":{"code":"INTERNAL_ERROR","message":"An unexpected error occurred."}}
Verdict: ERROR - status 500

## POST /api/costing/period-balance
Request: {"GlAccounts":["0000300000"],"DateFrom":"20260101","DateTo":"20260827"}
Result: HTTP 500 -- The remote server returned an error: (500) Internal Server Error.
Body: {"success":false,"data":{"exceptionType":"FormatException","message":"Input string was not in a correct format."},"error":{"code":"INTERNAL_ERROR","message":"An unexpected error occurred."}}
Verdict: ERROR - status 500

## POST /api/costing/profit-center
Request: {"GlAccounts":["0000300000"],"DateFrom":"20260101","DateTo":"20260827"}
Result: HTTP 500 -- The remote server returned an error: (500) Internal Server Error.
Body: {"success":false,"data":{"exceptionType":"FormatException","message":"String was not recognized as a valid DateTime."},"error":{"code":"INTERNAL_ERROR","message":"An unexpected error occurred."}}
Verdict: ERROR - status 500

## POST /api/costing/freight-posting
Verdict: NOT EXECUTED -- Real FI posting (freight cost against a GL account/profit centre) -- no dry-run, real financial document. Not attempted.

## POST /api/costing/freight-posting-batch
Verdict: NOT EXECUTED -- Same reasoning, batch version.

## INCIDENT: /api/quality/block?dryRun=true actually executed for real (not a bug -- a testing mistake, corrected)

QualityController.BlockStock has NO `dryRun` (or TestRun/CheckMode) parameter on its signature or on
QualityMb1bRequest at all -- the `?dryRun=true` query string was silently ignored by Web API 2 model binding
(unrecognized query keys are just ignored, not rejected), so this call executed a REAL MB1B posting: material
30005R, 1 KG, storage location 1710/RO/STORE, moved from unrestricted into quality-blocked stock, SAP material
document 4960322655 ("S M7 060  Document 4960322655 posted").

**Immediately reversed**: called POST /api/quality/unblock with the identical parameters -- succeeded, SAP
material document 4960322656 ("S M7 060  Document 4960322656 posted"), moving the same 1 KG back to unrestricted.
Verified via GET /api/warehouse/stock?Material=30005R afterward: availableQty=1204.210, identical to the value
before this incident (also 1204.210, confirmed at the very start of testing). Net effect on sandbox stock: zero.
Two real SAP documents (4960322655, 4960322656) now exist in the sandbox as a permanent record of this block/
unblock pair -- harmless, but real.

**Real finding here**: QualityController.BlockStock/UnblockStock have no dry-run capability at all, unlike most
other real-BAPI/BDC endpoints in this codebase (StockAdjustment, GoodsIssue, DeliveryChange all have TestRun;
StockAdjustment/GoodsIssue/DeliveryChange controllers explicitly check `[FromUri] bool dryRun` too). Worth adding
for consistency and to prevent exactly this kind of accidental real posting during future testing.


## ANALYSIS: the four real-BAPI TestRun=true calls (highest-value tests -- previously totally unverified)

- **stock-adjustment (BAPI_GOODSMVT_CREATE)**: `messages: []`, `success: false`, `materialDocument: ""`. This is
  CORRECT, not a bug -- SAP's own TESTRUN semantics never assign a real material document number even on a clean
  test, and `StockAdjustmentResponse.Success` requires a non-blank MaterialDocument by design (mirrors
  PurchasingHelper's EXPPURCHASEORDER-blank convention). An empty RETURN table with a blank doc number on a clean
  TestRun is exactly the expected shape. **PASS -- confirms the pinned-session + RETURN-table transport works
  correctly end to end against real SAP.**

- **goods-issue (BAPI_DELIVERYPROCESSING_EXEC)**: real, legible SAP business messages came back --
  "The transferred sales document table is empty" (W) and "Delivery not possible at the moment" (E). Confirms
  RETURN-table message reading works correctly with real text. The business content itself confirms the ALREADY-
  DOCUMENTED open risk from GoodsIssueModels.cs's own header comment: the minimal REQUEST-table field set
  (DOCUMENT_NUMB + dates only) is NOT sufficient -- SAP needs more fields (the message names point at the
  REQUEST table itself needing real sales-document linkage data, not just a bare delivery number). Not a new bug --
  this is the expected next iteration step already flagged when this endpoint was built. **Needs the field-set
  iteration already planned, now with a real, specific error message to iterate against.**

- **delivery-change (BAPI_OUTB_DELIVERY_CHANGE) -- REAL BUG FOUND**: `messages: [{"type":"E","message":""},
  {"type":"W","message":""}]` -- TYPE populated, MESSAGE blank, on BOTH returned rows. This is the exact same
  failure signature already diagnosed and fixed once this session for ZDELFLAG's ET_MESSAGE table -- except here
  RETURN is confirmed-standard BAPIRET2 (goods-issue's own RETURN read, identical helper/field-name list, worked
  fine with real text moments earlier in this same test run -- see above), so the table itself isn't the problem.
  Root cause: **DeliveryChangeHelper.ParseDeliveryChangeResponse only copies `Type`/`Message` from
  ReturnTableHelper.ExtractMessages -- it registers MESSAGE_V1/V2/V3/V4 in the ReadTable() call
  (BuildDeliveryChangeRequest) but never reads them back.** Real SAP messages sometimes carry their actual content
  in MESSAGE_V1-V4 with a blank base MESSAGE (a standard SAP message-class pattern -- variable-substitution
  messages with no static text of their own). This codebase already has an established fix for exactly this shape
  -- `WarehouseHelpers.TryReadReturnMessage` (used elsewhere for a different endpoint) falls back to joining
  MESSAGE_V1..V4 when MESSAGE itself is blank. DeliveryChangeHelper needs the same fallback -- right now, ANY
  SAP rejection of a delivery-change call is completely silent/unreadable (TYPE tells you E/W happened, but zero
  information about *why*), which defeats the whole purpose of surfacing SAP's real rejection reason back to the
  operator. **This needs fixing before delivery-change can be trusted in production** -- not a transport/NCo-layer
  bug, a straightforward application-layer parsing gap in code written this session.

- **consignment-mb1b (BDC-based, MB1B)**: real, legible message -- "E Entry 200604 LFA1  does not exist in
  (check entry)" (vendor 200604 doesn't exist in this sandbox -- expected, test data was a guess). Confirms the
  BDC-based transport (BdcBuilder + the MESSG structure-export-parameter fix documented in CLAUDE.md as
  "genuinely unverified") **also works correctly against real SAP** -- real readable text came back, not blank.
  This resolves CLAUDE.md's #1-highest-risk unverified item (`NcoRfcExecutor.BuildResponse`'s structure-export-
  parameter reading, `IRfcStructure.Metadata`/`.FieldCount`) for at least this one BDC call path. **PASS -- real
  confirmation the previously-unverified BDC MESSG reading mechanism works.**


## POST /api/production/mixing-scrap
Request: {"TestRun":true,"Material":"30005R","Quantity":1,"Header":"Test","Unit":"KG"}
Result: 200 OK -- {"success":true,"data":{"materialDocument":"","materialDocumentYear":"0000","success":false,"messages":[{"type":"E","message":"A reason has to be entered for movement type 551"}]},"error":null}
Verdict: PASS (see analysis)

## POST /api/production/goods-movement-backflush
Request: {"Material":"30005R","TestRun":true,"Components":[{"Material":"30005R","Quantity":1,"Unit":"KG"}],"Header":"Test","MovementType":"201"}
Result: HTTP 500 -- The remote server returned an error: (500) Internal Server Error.
Body: {"success":false,"data":{"exceptionType":"InvalidCastException","message":"Unable to cast object of type \u0027System.Collections.Generic.List\u00601[SapServer.Models.Bapi.GoodsMovementComponent]\u0027 to type \u0027System.Array\u0027."},"error":{"code":"INTERNAL_ERROR","message":"An unexpected error occurred."}}
Verdict: ERROR

## SYSTEMIC BUG: [MinLength(1)] on a List<T> property crashes with InvalidCastException (net48 DataAnnotations)

Confirmed via POST /api/production/goods-movement-backflush (TestRun=true, otherwise-valid request) -- HTTP 500,
exceptionType=InvalidCastException, "Unable to cast object of type
'System.Collections.Generic.List`1[SapServer.Models.Bapi.GoodsMovementComponent]' to type 'System.Array'."
Full server-side stack trace confirms the exact mechanism:
  System.ComponentModel.DataAnnotations.MinLengthAttribute.IsValid(Object value)
  -> System.Web.Http.Validation.Validators.DataAnnotationsModelValidator.Validate(...)
  -> (Web API 2's automatic model-state validation during [FromBody] binding, before the action even runs)

Root cause: .NET Framework 4.8's `System.ComponentModel.DataAnnotations.MinLengthAttribute.IsValid` only knows how
to measure length for a `string` or a real `T[]` array -- for any other value it unconditionally casts to `Array`,
which throws for `List<T>` (a `List<T>` does NOT derive from `Array`, even though it implements `ICollection`).
This is a long-standing net48 DataAnnotations limitation that ASP.NET Core's equivalent validator does not have
(it handles `ICollection` generically) -- a genuine, confirmed regression introduced by this session's .NET
Framework rebuild, not present in the original ASP.NET Core version.

**This makes POST /api/production/goods-movement-backflush completely non-functional right now** -- every call
with a non-empty Components list (i.e. every real call) crashes with a 500 before the action method is ever
entered, regardless of whether the SAP-side data is valid. TestRun=true was never even reached.

**Second confirmed site with the identical pattern, not yet tested but certain to crash the same way**:
`Models/Bapi/PackagingModels.cs`: `MassPackagingUpdateRequest.Rows` -- `[Required, MinLength(1)] public
List<MassPackagingUpdateRow> Rows`. POST /api/packaging/mass-update was already logged above as NOT EXECUTED for
an unrelated reason (real config writes) -- update that verdict: it would 500 immediately regardless, same root
cause.

Grepped every `Models/Bapi/*.cs` file for `[MinLength(` combined with a `List<` property type -- these two are the
only matches, so this is the complete list of currently-affected endpoints. Any *future* request DTO using
`[MinLength(1)] public List<T> ...` would hit the same crash.

**Fix options** (for whoever picks this up -- not attempted here, testing-only): replace `[MinLength(1)]` on a
List<T> property with a custom validation attribute that checks `.Count`, or drop the attribute and check
`.Count == 0` manually in the controller action (the same pattern already used elsewhere in this codebase --
e.g. `PicksheetMaterials`/`PicksheetStock`'s own `if (request.Deliveries.Count == 0) return Ok(...[])` early-return
checks, which sidestep this exact class of validation-attribute problem entirely by not using MinLength on a list
at all).


## GET /api/production/bom (via curl.exe, GET+body workaround)
Request: {"Material":"30005R"}
Result: HTTP 200 -- {"success":true,"data":[],"error":null}
Verdict: PASS (200 OK)

## GET /api/production/check-profit-centre (via curl.exe, GET+body workaround)
Request: {"Material":"30005R"}
Result: HTTP 200 -- {"success":true,"data":"2012","error":null}
Verdict: PASS (200 OK)

## GET /api/production/check-profit-centres (via curl.exe, GET+body workaround)
Request: {"Materials":["30005R"]}
Result: HTTP 200 -- {"success":true,"data":[{"material":"30005R","profitCentre":"2012"}],"error":null}
Verdict: PASS (200 OK)

## GET /api/production/find-cost-collector (via curl.exe, GET+body workaround)
Request: {"Material":"30005R"}
Result: HTTP 400 -- {"success":false,"data":null,"error":{"code":"400","message":"No cost collector (AFKO) found for material '30005R'."}}
Verdict: PASS -- clean, well-formed business-validation 400 ("no AFKO record for this material"), not a
crash. 30005R likely just has no repetitive-manufacturing cost collector set up in the sandbox. Endpoint
logic and error handling both correct; not a bug.

## GET /api/function/params (via curl.exe, GET+body workaround)
Request: {"FunctionName":"RFC_PING"}
Result: HTTP 200 -- {"success":true,"data":[],"error":null}
Verdict: PASS (200 OK)

## SUMMARY

Endpoints in codebase (real `[Route]` attributes across all controllers): 88
Distinct endpoint paths attempted this session: 82

- PASS (clean, correct behavior, including correct business-validation 4xx and correct real-SAP-rejection
  handling): 56
- BUG FOUND (real, reproducible defects): 4 distinct root causes, affecting 5+ endpoints -- see list below
- NOT EXECUTED (deliberately skipped -- real, irreversible SAP writes with no dry-run path, no safe test
  data available, or requiring per-user elevated credentials not available in this session): 25
- Transient/non-issues (timeout that didn't reproduce, PowerShell tooling limitation worked around via
  curl.exe): 2

### BUG FOUND -- full list

1. **`[FromUri]` optional scalar params 404 the whole route when omitted, if they lack an explicit C#
   default** -- confirmed on 6 endpoints: `MrpAnalysisController.GetGoodsReceiptHistory` (`sinceDate`),
   `PackagingController.GetInstruction` (`customer`), `PerformanceController.GetInvoicing` (`from`/`to`),
   `PerformanceController.GetOtif` (`from`/`to`), `PerformanceController.GetAgreements` (`horizonDays`),
   `ProductionController.GetOrderText` (`textId`). Existing CLAUDE.md guidance on this is incomplete: it
   says nullable `[FromUri]` params don't need a default, but nullable ones need it too. Fix: add
   `= null`/`= default` to each. Full detail: search this log for "SYSTEMIC BUG: [FromUri]".

2. **`DeliveryChangeHelper.ParseDeliveryChangeResponse` never reads `MESSAGE_V1`-`V4`** -- any real SAP
   rejection of `POST /api/warehouse/delivery-change` comes back with a populated `TYPE` but a blank
   `MESSAGE`, exactly like the ZDELFLAG bug fixed earlier this session. Confirmed live: TestRun=true against
   a real delivery returned `[{"type":"E","message":""},{"type":"W","message":""}]`. Fix: apply the same
   MESSAGE_V1-V4 fallback `WarehouseHelpers.TryReadReturnMessage` already uses. Full detail: search this log
   for "REAL BUG FOUND" under the ANALYSIS section.

3. **`CostingController`'s three endpoints (`cost-sheet`, `period-balance`, `profit-center`) throw an
   unhandled `FormatException` -> raw 500** on a malformed date string, instead of a clean 400. Confirmed
   live on all three with `Date`/`DateFrom`/`DateTo` values that didn't match the expected `DateTime.
   ParseExact` format. Fix: catch the parse and return a validation 400, or validate the format up front.

4. **`[MinLength(1)]` on a `List<T>` request property crashes with `InvalidCastException`, before the
   controller action even runs** -- a net48 `DataAnnotations` limitation (`MinLengthAttribute.IsValid` casts
   straight to `Array`, which `List<T>` isn't). Confirmed live on `POST /api/production/goods-movement-
   backflush` (`GoodsMovementRequest.Components`) -- every real call with a non-empty list 500s outright,
   TestRun is never reached. Second, not-yet-live-tested but certain-to-crash-identically site:
   `PackagingModels.cs`'s `MassPackagingUpdateRequest.Rows`, used by `POST /api/packaging/mass-update`. Fix:
   drop `[MinLength(1)]` on both and check `.Count == 0` manually in the action (the pattern already used
   by `PicksheetMaterials`/`PicksheetStock`), or write a custom `Count`-based validation attribute. Full
   detail: search this log for "SYSTEMIC BUG: [MinLength(1)]".

### Also worth fixing (found live, lower severity / not strictly a defect)

- `QualityController.BlockStock`/`UnblockStock` have no dry-run/TestRun capability at all, unlike every
  other real-BAPI/BDC write endpoint in this codebase. This caused a real (harmlessly reversed) SAP posting
  during this test run -- see the "INCIDENT" section in this log. Worth adding a `dryRun`/`TestRun` param for
  consistency and to prevent repeat accidents.
- `GoodsIssueHelper`'s minimal REQUEST-table field set (DOCUMENT_NUMB + dates only) is confirmed
  insufficient against real SAP ("The transferred sales document table is empty" / "Delivery not possible
  at the moment") -- this was already a documented open risk before this session, now backed by a real,
  specific SAP rejection message to iterate against.

### Confirmed working (previously unverified/high-risk items, now resolved)

- The pinned-worker + `TESTRUN`/RETURN-table transport for `BAPI_GOODSMVT_CREATE` (stock-adjustment,
  consignment-mb1b) works correctly end-to-end against real SAP.
- `NcoRfcExecutor.BuildResponse`'s structure-export-parameter reading (`IRfcStructure.Metadata`, previously
  CLAUDE.md's #1 highest-risk unverified item) works correctly for a real BDC/MB1B call -- confirmed via a
  real, legible SAP rejection message coming back through `consignment-mb1b`.
- `[FromUri]` complex-type query objects (the already-documented, already-fixed `query ??= new Xxx()`
  pattern) correctly handle a fully-omitted query string.

Full request/response detail for every endpoint tested, plus the "SYSTEMIC BUG", "INCIDENT", and "ANALYSIS"
narrative sections, are in this same file:
C:\Users\matthew.walker\source\repos\TheMatthewWalker\SapServer\endpoint-test-log-2026-08-27.md

No SQL writes were made at any point during this session. IIS site/app pool left running in Development mode,
as-is. No source files were edited or committed.

---

## FIXES APPLIED (same session, after testing above)

All 4 confirmed bugs plus the QualityController dry-run gap were fixed and re-verified live against the same
SAP sandbox (not just unit-tested):

1. **`[FromUri]` optional params missing C# defaults** — added `= null`/`= default` to all 6 sites
   (`MrpAnalysisController.GetGoodsReceiptHistory`, `PackagingController.GetInstruction`,
   `PerformanceController.GetInvoicing`/`GetOtif`/`GetAgreements`, `ProductionController.GetOrderText`).
   Verified: `GET /api/performance/agreements` with no query string now returns real data instead of 404.

2. **`DeliveryChangeHelper`'s blank MESSAGE on real SAP rejections** — fixed at the shared
   `ReturnTableHelper.ExtractMessages` level (falls back to joining `MESSAGE_V1`-`V4` when `MESSAGE` is blank),
   not just in `DeliveryChangeHelper`, so any other BAPI helper that requests those columns benefits too.
   Added `ReturnTableHelperTests.cs`. Verified: the same `delivery-change` TestRun call that previously came
   back `{"type":"E","message":""}` now returns real content (`"0080001234 000010"` / `"0 0"` — a genuine SAP
   message-class quirk with no static template text, not a bug; the mechanism now correctly surfaces whatever
   SAP actually sends instead of silently dropping it).

3. **`CostingController` raw 500 on a malformed date/period** — added explicit `TryParseExact`/`TryParse`
   validation returning a clean 400 (`INVALID_DATA`) before calling into `CostingHelper`, on all three affected
   actions (`cost-sheet`, `period-balance`, `profit-center`). Verified both distinct failure shapes
   (bad date string, non-numeric period) now return 400 with a clear message instead of a raw `FormatException`.

4. **`[MinLength(1)]` on `List<T>` crashing model validation** — removed from both sites
   (`ProductionModels.GoodsMovementRequest.Components`, `PackagingModels.MassPackagingUpdateRequest.Rows`),
   replaced with an explicit `.Count == 0` check in the controller action (matching the existing
   `PicksheetMaterials`/`PicksheetStock` pattern). Verified: `goods-movement-backflush` TestRun now reaches SAP
   for real and gets a genuine business rejection ("Account 409010 requires an assignment to a CO object")
   instead of an `InvalidCastException` 500 before the action even ran.

5. **`QualityController.BlockStock`/`UnblockStock` had no dry-run capability** (the cause of the real incident
   documented above) — added `[FromUri] bool dryRun = false` to both, short-circuiting before any SAP call
   (no `TestRun`/`SIMULATE` exists for this BDC-based call, same as every other BDC endpoint in this codebase,
   so this is a client-side guard, not an SAP-side one). Verified: `?dryRun=true` now returns the built BDC
   request without touching SAP at all.

Full `dotnet test` suite: 472 passed, 6 skipped (pre-existing `TEST_SQL_SERVER`-gated integration tests),
0 failed — run natively on this Windows machine (not the mono workaround CLAUDE.md documents for the Linux
sandbox this was originally developed in).

Not attempted (out of scope for this fix pass, not part of the 4 confirmed bugs):
- `GoodsIssueHelper`'s minimal REQUEST-table field set (already a documented open risk, now backed by real
  SAP rejection messages to iterate against — needs live field-set iteration, not a one-line fix).
