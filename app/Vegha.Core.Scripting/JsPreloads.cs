namespace Vegha.Core.Scripting;

/// <summary>
/// Tiny pure-JS stand-ins for the libraries Bruno scripts commonly assume are global.
/// Embedding lodash + axios as full ports is overkill for the API testing use case —
/// these implement the helpers users actually reach for in pre/post scripts:
/// <c>_.get / _.set / _.cloneDeep / _.isEmpty / _.pick / _.omit</c> and an
/// <c>axios</c> object whose <c>get/post/put/delete</c> delegate to <c>bru.sendRequest</c>.
///
/// AJV (JSON-schema validation) requires a real implementation; deferred to v1 along
/// with the assert-runtime parity work.
/// </summary>
public static class JsPreloads
{
    /// <summary>Concatenated source of every preload script. Injected into the Jint engine
    /// before any user script runs. Pure JS — no host-side bindings beyond <c>bru</c>.</summary>
    public const string Source = LodashLite + "\n" + AxiosShim;

    private const string LodashLite = """
        var _ = {
            // _.get(obj, 'a.b.c', defaultValue) — safe nested-property access.
            get: function (obj, path, def) {
                if (obj == null) return def;
                var parts = typeof path === 'string' ? path.split('.') : path;
                var cur = obj;
                for (var i = 0; i < parts.length; i++) {
                    if (cur == null) return def;
                    cur = cur[parts[i]];
                }
                return cur === undefined ? def : cur;
            },
            // _.set(obj, 'a.b.c', value) — mutates in place, creating intermediate objects.
            set: function (obj, path, value) {
                if (obj == null) return obj;
                var parts = typeof path === 'string' ? path.split('.') : path;
                var cur = obj;
                for (var i = 0; i < parts.length - 1; i++) {
                    if (cur[parts[i]] == null) cur[parts[i]] = {};
                    cur = cur[parts[i]];
                }
                cur[parts[parts.length - 1]] = value;
                return obj;
            },
            cloneDeep: function (obj) {
                return JSON.parse(JSON.stringify(obj));
            },
            isEmpty: function (v) {
                if (v == null) return true;
                if (typeof v === 'string' || Array.isArray(v)) return v.length === 0;
                if (typeof v === 'object') {
                    for (var k in v) { if (Object.prototype.hasOwnProperty.call(v, k)) return false; }
                    return true;
                }
                return false;
            },
            pick: function (obj, keys) {
                var result = {};
                if (obj == null) return result;
                for (var i = 0; i < keys.length; i++) {
                    if (Object.prototype.hasOwnProperty.call(obj, keys[i])) result[keys[i]] = obj[keys[i]];
                }
                return result;
            },
            omit: function (obj, keys) {
                var result = {};
                if (obj == null) return result;
                var skip = {};
                for (var i = 0; i < keys.length; i++) skip[keys[i]] = true;
                for (var k in obj) {
                    if (Object.prototype.hasOwnProperty.call(obj, k) && !skip[k]) result[k] = obj[k];
                }
                return result;
            },
            forEach: function (collection, fn) {
                if (Array.isArray(collection)) {
                    for (var i = 0; i < collection.length; i++) fn(collection[i], i, collection);
                } else if (collection != null && typeof collection === 'object') {
                    for (var k in collection) {
                        if (Object.prototype.hasOwnProperty.call(collection, k)) fn(collection[k], k, collection);
                    }
                }
                return collection;
            },
            map: function (collection, fn) {
                var out = [];
                if (Array.isArray(collection)) {
                    for (var i = 0; i < collection.length; i++) out.push(fn(collection[i], i, collection));
                } else if (collection != null && typeof collection === 'object') {
                    for (var k in collection) {
                        if (Object.prototype.hasOwnProperty.call(collection, k)) out.push(fn(collection[k], k, collection));
                    }
                }
                return out;
            },
            filter: function (collection, fn) {
                var out = [];
                if (Array.isArray(collection)) {
                    for (var i = 0; i < collection.length; i++) {
                        if (fn(collection[i], i, collection)) out.push(collection[i]);
                    }
                }
                return out;
            }
        };
        """;

    private const string AxiosShim = """
        // Minimal axios-style shim: calls go through bru.sendRequest so requests
        // honor the same cookie jar, timing, and authentication chain as the main flow.
        var axios = {
            request: function (config) {
                var resp = bru.sendRequest({
                    Method: config.method || 'GET',
                    Url: config.url,
                    Headers: config.headers || {},
                    Body: typeof config.data === 'string' ? config.data : (config.data == null ? null : JSON.stringify(config.data)),
                    ContentType: (config.headers && (config.headers['content-type'] || config.headers['Content-Type'])) || 'application/json'
                });
                var dataParsed;
                try { dataParsed = JSON.parse(resp.Body); }
                catch (e) { dataParsed = resp.Body; }
                return {
                    status: resp.Status,
                    statusText: resp.StatusText,
                    headers: resp.Headers,
                    data: dataParsed
                };
            },
            get: function (url, config) {
                return axios.request(Object.assign({}, config || {}, { method: 'GET', url: url }));
            },
            post: function (url, data, config) {
                return axios.request(Object.assign({}, config || {}, { method: 'POST', url: url, data: data }));
            },
            put: function (url, data, config) {
                return axios.request(Object.assign({}, config || {}, { method: 'PUT', url: url, data: data }));
            },
            patch: function (url, data, config) {
                return axios.request(Object.assign({}, config || {}, { method: 'PATCH', url: url, data: data }));
            },
            delete: function (url, config) {
                return axios.request(Object.assign({}, config || {}, { method: 'DELETE', url: url }));
            }
        };
        """;
}
