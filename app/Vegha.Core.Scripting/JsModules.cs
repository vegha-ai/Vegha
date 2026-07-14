namespace Vegha.Core.Scripting;

/// <summary>
/// Postman-compatible <c>require(...)</c> module registry plus the globals Postman injects
/// (<c>xml2Json</c>, <c>atob</c>, <c>btoa</c>). Postman-collection scripts assume a Node-like
/// sandbox with a fixed set of bundled libraries; scripts imported into Vegha die with
/// <c>ReferenceError: require is not defined</c> without this shim.
///
/// Correctness-critical work (hashing, HMAC, XML parsing, base64) delegates to the C#
/// <see cref="ScriptModuleHost"/> bound as <c>__vegha</c>; everything else is pure JS. The
/// light utility libraries (lodash, moment, uuid, chai, tv4/ajv, csv-parse, cheerio, and the
/// common Node core modules) are implemented here at the fidelity real Postman test scripts
/// actually exercise. Heavy modules that don't map to the API-testing use case
/// (<c>postman-collection</c>, <c>stream</c>, <c>zlib</c>, <c>timers</c>) throw a clear,
/// listing error rather than Node's cryptic failure.
///
/// Injected AFTER <see cref="JsPreloads"/> so it can extend the existing lodash-lite <c>_</c>.
/// </summary>
public static class JsModules
{
    /// <summary>Concatenated module source. Requires <c>__vegha</c> (a <see cref="ScriptModuleHost"/>)
    /// and the lodash-lite <c>_</c> from <see cref="JsPreloads"/> to already be in scope.</summary>
    public const string Source = LodashExtras + "\n" + ModuleRegistry;

    // Extends the lodash-lite `_` from JsPreloads with the additional helpers real Postman
    // scripts reach for. require('lodash') returns this same object.
    private const string LodashExtras = """
        (function () {
            var L = _;
            function isObj(v) { return v != null && (typeof v === 'object' || typeof v === 'function'); }
            L.isArray = Array.isArray;
            L.isObject = isObj;
            L.isString = function (v) { return typeof v === 'string'; };
            L.isNumber = function (v) { return typeof v === 'number'; };
            L.isBoolean = function (v) { return typeof v === 'boolean'; };
            L.isFunction = function (v) { return typeof v === 'function'; };
            L.isNil = function (v) { return v == null; };
            L.isNull = function (v) { return v === null; };
            L.isUndefined = function (v) { return v === undefined; };
            L.isDate = function (v) { return v instanceof Date; };
            L.keys = function (o) { return o == null ? [] : Object.keys(o); };
            L.values = function (o) { return o == null ? [] : Object.keys(o).map(function (k) { return o[k]; }); };
            L.has = function (o, k) { return o != null && Object.prototype.hasOwnProperty.call(o, k); };
            L.size = function (o) {
                if (o == null) return 0;
                if (typeof o === 'string' || Array.isArray(o)) return o.length;
                return Object.keys(o).length;
            };
            L.includes = function (c, v) {
                if (c == null) return false;
                if (typeof c === 'string') return c.indexOf(v) >= 0;
                if (Array.isArray(c)) return c.indexOf(v) >= 0;
                return L.values(c).indexOf(v) >= 0;
            };
            L.find = function (a, fn) { for (var i = 0; i < a.length; i++) if (fn(a[i], i, a)) return a[i]; return undefined; };
            L.findIndex = function (a, fn) { for (var i = 0; i < a.length; i++) if (fn(a[i], i, a)) return i; return -1; };
            L.some = function (a, fn) { for (var i = 0; i < a.length; i++) if (fn(a[i], i, a)) return true; return false; };
            L.every = function (a, fn) { for (var i = 0; i < a.length; i++) if (!fn(a[i], i, a)) return false; return true; };
            L.reduce = function (a, fn, acc) {
                var start = 0;
                if (acc === undefined) { acc = a[0]; start = 1; }
                for (var i = start; i < a.length; i++) acc = fn(acc, a[i], i, a);
                return acc;
            };
            L.uniq = function (a) {
                var out = [];
                for (var i = 0; i < a.length; i++) if (out.indexOf(a[i]) < 0) out.push(a[i]);
                return out;
            };
            L.compact = function (a) { return a.filter(function (v) { return !!v; }); };
            L.flatten = function (a) {
                var out = [];
                for (var i = 0; i < a.length; i++) { if (Array.isArray(a[i])) out.push.apply(out, a[i]); else out.push(a[i]); }
                return out;
            };
            L.flattenDeep = function fd(a) {
                var out = [];
                for (var i = 0; i < a.length; i++) { if (Array.isArray(a[i])) out.push.apply(out, fd(a[i])); else out.push(a[i]); }
                return out;
            };
            L.chunk = function (a, n) {
                n = n || 1; var out = [];
                for (var i = 0; i < a.length; i += n) out.push(a.slice(i, i + n));
                return out;
            };
            L.head = L.first = function (a) { return a == null ? undefined : a[0]; };
            L.last = function (a) { return a == null || a.length === 0 ? undefined : a[a.length - 1]; };
            L.take = function (a, n) { return a.slice(0, n === undefined ? 1 : n); };
            L.drop = function (a, n) { return a.slice(n === undefined ? 1 : n); };
            L.without = function (a) {
                var rest = Array.prototype.slice.call(arguments, 1);
                return a.filter(function (v) { return rest.indexOf(v) < 0; });
            };
            L.difference = function (a, b) { return a.filter(function (v) { return (b || []).indexOf(v) < 0; }); };
            L.intersection = function (a, b) { return a.filter(function (v) { return (b || []).indexOf(v) >= 0; }); };
            L.union = function () {
                var out = [];
                for (var i = 0; i < arguments.length; i++) {
                    var arr = arguments[i] || [];
                    for (var j = 0; j < arr.length; j++) if (out.indexOf(arr[j]) < 0) out.push(arr[j]);
                }
                return out;
            };
            function iteratee(it) {
                if (typeof it === 'function') return it;
                if (typeof it === 'string') return function (o) { return L.get(o, it); };
                return function (o) { return o; };
            }
            L.groupBy = function (a, it) {
                var fn = iteratee(it), out = {};
                for (var i = 0; i < a.length; i++) { var k = fn(a[i]); (out[k] = out[k] || []).push(a[i]); }
                return out;
            };
            L.keyBy = function (a, it) {
                var fn = iteratee(it), out = {};
                for (var i = 0; i < a.length; i++) out[fn(a[i])] = a[i];
                return out;
            };
            L.sortBy = function (a, it) {
                var fn = iteratee(it);
                return a.slice().sort(function (x, y) { var fx = fn(x), fy = fn(y); return fx < fy ? -1 : fx > fy ? 1 : 0; });
            };
            L.mapValues = function (o, it) {
                var fn = iteratee(it), out = {};
                for (var k in o) if (L.has(o, k)) out[k] = fn(o[k], k);
                return out;
            };
            L.invert = function (o) { var out = {}; for (var k in o) if (L.has(o, k)) out[o[k]] = k; return out; };
            L.range = function (start, end, step) {
                if (end === undefined) { end = start; start = 0; }
                step = step || 1; var out = [];
                if (step > 0) for (var i = start; i < end; i += step) out.push(i);
                else for (var j = start; j > end; j += step) out.push(j);
                return out;
            };
            L.times = function (n, fn) { var out = []; for (var i = 0; i < n; i++) out.push(fn(i)); return out; };
            L.sum = function (a) { return a.reduce(function (s, v) { return s + v; }, 0); };
            L.sumBy = function (a, it) { var fn = iteratee(it); return a.reduce(function (s, v) { return s + fn(v); }, 0); };
            L.max = function (a) { return a == null || a.length === 0 ? undefined : Math.max.apply(Math, a); };
            L.min = function (a) { return a == null || a.length === 0 ? undefined : Math.min.apply(Math, a); };
            L.clamp = function (n, lower, upper) { return Math.min(Math.max(n, lower), upper); };
            L.assign = L.extend = function (dst) {
                for (var i = 1; i < arguments.length; i++) { var s = arguments[i]; for (var k in s) if (L.has(s, k)) dst[k] = s[k]; }
                return dst;
            };
            L.defaults = function (dst) {
                for (var i = 1; i < arguments.length; i++) { var s = arguments[i]; for (var k in s) if (L.has(s, k) && dst[k] === undefined) dst[k] = s[k]; }
                return dst;
            };
            L.merge = function mergeFn(dst) {
                for (var i = 1; i < arguments.length; i++) {
                    var s = arguments[i];
                    for (var k in s) {
                        if (!L.has(s, k)) continue;
                        if (isObj(s[k]) && !Array.isArray(s[k]) && isObj(dst[k])) mergeFn(dst[k], s[k]);
                        else dst[k] = s[k];
                    }
                }
                return dst;
            };
            L.isEqual = function eq(a, b) {
                if (a === b) return true;
                if (a == null || b == null) return a === b;
                if (typeof a !== 'object' || typeof b !== 'object') return a === b;
                var ka = Object.keys(a), kb = Object.keys(b);
                if (ka.length !== kb.length) return false;
                for (var i = 0; i < ka.length; i++) { if (!eq(a[ka[i]], b[ka[i]])) return false; }
                return true;
            };
            L.capitalize = function (s) { s = String(s == null ? '' : s); return s.charAt(0).toUpperCase() + s.slice(1).toLowerCase(); };
            L.upperFirst = function (s) { s = String(s == null ? '' : s); return s.charAt(0).toUpperCase() + s.slice(1); };
            L.trim = function (s, ch) { return ch === undefined ? String(s).trim() : String(s).replace(new RegExp('^[' + ch + ']+|[' + ch + ']+$', 'g'), ''); };
            L.noop = function () {};
            L.identity = function (v) { return v; };
        })();
        """;

    // require() + the Postman-injected globals. `modules` is a lazy registry: factories run
    // once and cache, matching Node's module singleton semantics.
    private const string ModuleRegistry = """
        var __modules = {};
        var __moduleCache = {};

        function require(name) {
            if (Object.prototype.hasOwnProperty.call(__moduleCache, name))
                return __moduleCache[name];
            var factory = __modules[name];
            if (!factory) {
                throw new Error(
                    "Cannot find module '" + name + "'. Vegha bundles the Postman sandbox libraries: " +
                    "lodash, moment, uuid, crypto-js, chai, tv4, ajv, cheerio, csv-parse/lib/sync, xml2js, " +
                    "atob, btoa, and the Node core modules url, querystring, util, path, buffer, assert, events.");
            }
            var exports = factory();
            __moduleCache[name] = exports;
            return exports;
        }

        // ----- lodash -----
        __modules['lodash'] = function () { return _; };

        // ----- uuid (v4 backed by the host RNG) -----
        __modules['uuid'] = function () {
            var v4 = function () { return __vegha.uuid(); };
            var api = function () { return v4(); };
            api.v4 = v4;
            // No true v1 in the sandbox; alias to v4 so require('uuid').v1() doesn't explode.
            api.v1 = v4;
            api.NIL = '00000000-0000-0000-0000-000000000000';
            api.validate = function (s) {
                return typeof s === 'string' &&
                    /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(s);
            };
            api.version = function (s) { return api.validate(s) ? parseInt(s.charAt(14), 16) : null; };
            return api;
        };

        // ----- atob / btoa (as modules AND globals) -----
        function atob(s) { return __vegha.atob(String(s)); }
        function btoa(s) { return __vegha.btoa(String(s)); }
        __modules['atob'] = function () { return atob; };
        __modules['btoa'] = function () { return btoa; };

        // ----- crypto-js -----
        __modules['crypto-js'] = function () {
            function wrap(hex) {
                return {
                    __hex: hex,
                    toString: function (enc) {
                        if (enc && enc.__name === 'Base64') return __vegha.hexToBase64(hex);
                        return hex; // default + enc.Hex
                    }
                };
            }
            function coerce(v) {
                if (v == null) return '';
                if (typeof v === 'string') return v;
                if (v.__text !== undefined) return v.__text;
                return String(v);
            }
            var C = {
                MD5: function (m) { return wrap(__vegha.hash('md5', coerce(m))); },
                SHA1: function (m) { return wrap(__vegha.hash('sha1', coerce(m))); },
                SHA256: function (m) { return wrap(__vegha.hash('sha256', coerce(m))); },
                SHA384: function (m) { return wrap(__vegha.hash('sha384', coerce(m))); },
                SHA512: function (m) { return wrap(__vegha.hash('sha512', coerce(m))); },
                HmacMD5: function (m, k) { return wrap(__vegha.hmac('md5', coerce(m), coerce(k))); },
                HmacSHA1: function (m, k) { return wrap(__vegha.hmac('sha1', coerce(m), coerce(k))); },
                HmacSHA256: function (m, k) { return wrap(__vegha.hmac('sha256', coerce(m), coerce(k))); },
                HmacSHA384: function (m, k) { return wrap(__vegha.hmac('sha384', coerce(m), coerce(k))); },
                HmacSHA512: function (m, k) { return wrap(__vegha.hmac('sha512', coerce(m), coerce(k))); },
                enc: {
                    Hex: { __name: 'Hex' },
                    Base64: { __name: 'Base64' },
                    Utf8: {
                        __name: 'Utf8',
                        parse: function (s) { return { __text: s, toString: function () { return s; } }; },
                        stringify: function (wa) { return wa && wa.__text !== undefined ? wa.__text : String(wa); }
                    }
                }
            };
            C.AES = {
                encrypt: function () { throw new Error('crypto-js AES is not supported in the Vegha sandbox (hashing + HMAC are).'); },
                decrypt: function () { throw new Error('crypto-js AES is not supported in the Vegha sandbox (hashing + HMAC are).'); }
            };
            return C;
        };

        // ----- xml2js + the xml2Json global -----
        function xml2Json(xml) { return JSON.parse(__vegha.xmlToJson(String(xml == null ? '' : xml))); }
        __modules['xml2js'] = function () {
            function parseFromString(xml) { return JSON.parse(__vegha.xmlToJson(String(xml == null ? '' : xml))); }
            function Parser() {}
            // Real xml2js is async (callback last); we resolve synchronously since parsing is.
            Parser.prototype.parseString = function (xml, cb) {
                var result, err = null;
                try { result = parseFromString(xml); } catch (e) { err = e; }
                if (typeof cb === 'function') cb(err, result);
                return result;
            };
            Parser.prototype.parseStringPromise = function (xml) {
                return Promise.resolve(parseFromString(xml));
            };
            return {
                Parser: Parser,
                parseString: function (xml, cb) { return new Parser().parseString(xml, cb); },
                parseStringPromise: function (xml) { return Promise.resolve(parseFromString(xml)); }
            };
        };

        // ----- moment (light) -----
        __modules['moment'] = function () {
            var UNITS = {
                years: 'FullYear', year: 'FullYear', y: 'FullYear',
                months: 'Month', month: 'Month', M: 'Month',
                days: 'Date', day: 'Date', d: 'Date', date: 'Date',
                hours: 'Hours', hour: 'Hours', h: 'Hours',
                minutes: 'Minutes', minute: 'Minutes', m: 'Minutes',
                seconds: 'Seconds', second: 'Seconds', s: 'Seconds',
                milliseconds: 'Milliseconds', millisecond: 'Milliseconds', ms: 'Milliseconds'
            };
            var MS = { millisecond: 1, milliseconds: 1, ms: 1, second: 1000, seconds: 1000, s: 1000,
                minute: 60000, minutes: 60000, m: 60000, hour: 3600000, hours: 3600000, h: 3600000,
                day: 86400000, days: 86400000, d: 86400000, week: 604800000, weeks: 604800000, w: 604800000 };
            function pad(n, w) { n = String(Math.abs(n)); while (n.length < (w || 2)) n = '0' + n; return n; }
            function Moment(d) { this._d = d; this._valid = !isNaN(d.getTime()); }
            Moment.prototype.isValid = function () { return this._valid; };
            Moment.prototype.toDate = function () { return this._d; };
            Moment.prototype.valueOf = function () { return this._d.getTime(); };
            Moment.prototype.unix = function () { return Math.floor(this._d.getTime() / 1000); };
            Moment.prototype.toISOString = function () { return this._d.toISOString(); };
            Moment.prototype.toString = function () { return this._d.toString(); };
            Moment.prototype.clone = function () { return new Moment(new Date(this._d.getTime())); };
            Moment.prototype.get = function (u) { return this._d['get' + UNITS[u]](); };
            Moment.prototype.add = function (n, u) {
                if (u === 'weeks' || u === 'week' || u === 'w') { this._d.setDate(this._d.getDate() + n * 7); return this; }
                var m = UNITS[u]; this._d['set' + m](this._d['get' + m]() + n); return this;
            };
            Moment.prototype.subtract = function (n, u) { return this.add(-n, u); };
            Moment.prototype.diff = function (other, u) {
                var o = other instanceof Moment ? other._d : new Date(other);
                var ms = this._d.getTime() - o.getTime();
                return Math.trunc(ms / (MS[u] || 1));
            };
            Moment.prototype.isBefore = function (o) { return this.valueOf() < (o instanceof Moment ? o.valueOf() : new Date(o).getTime()); };
            Moment.prototype.isAfter = function (o) { return this.valueOf() > (o instanceof Moment ? o.valueOf() : new Date(o).getTime()); };
            Moment.prototype.format = function (fmt) {
                var d = this._d;
                if (!fmt) return d.toISOString();
                var map = {
                    YYYY: d.getFullYear(), YY: pad(d.getFullYear() % 100),
                    MM: pad(d.getMonth() + 1), M: d.getMonth() + 1,
                    DD: pad(d.getDate()), D: d.getDate(),
                    HH: pad(d.getHours()), H: d.getHours(),
                    mm: pad(d.getMinutes()), m: d.getMinutes(),
                    ss: pad(d.getSeconds()), s: d.getSeconds(),
                    SSS: pad(d.getMilliseconds(), 3)
                };
                return fmt.replace(/YYYY|YY|MM|M|DD|D|HH|H|mm|m|ss|s|SSS/g, function (t) { return map[t]; });
            };
            function moment(input) {
                if (input === undefined) return new Moment(new Date());
                if (input instanceof Moment) return input.clone();
                if (input instanceof Date) return new Moment(new Date(input.getTime()));
                if (typeof input === 'number') return new Moment(new Date(input));
                return new Moment(new Date(input));
            }
            moment.utc = function (input) { return moment(input); };
            moment.unix = function (secs) { return new Moment(new Date(secs * 1000)); };
            moment.now = function () { return new Date().getTime(); };
            moment.ISO_8601 = 'ISO_8601';
            return moment;
        };

        // ----- chai (expect + assert) -----
        __modules['chai'] = function () {
            function fail(msg) { throw new Error(msg); }
            function deepEqual(a, b) { return _.isEqual(a, b); }
            function Assertion(actual, negate) { this._a = actual; this._neg = !!negate; }
            function check(ctx, cond, msg, negMsg) {
                if (ctx._neg) { if (cond) fail(negMsg); } else { if (!cond) fail(msg); }
            }
            Object.defineProperty(Assertion.prototype, 'to', { get: function () { return this; } });
            Object.defineProperty(Assertion.prototype, 'be', { get: function () { return this; } });
            Object.defineProperty(Assertion.prototype, 'been', { get: function () { return this; } });
            Object.defineProperty(Assertion.prototype, 'is', { get: function () { return this; } });
            Object.defineProperty(Assertion.prototype, 'that', { get: function () { return this; } });
            Object.defineProperty(Assertion.prototype, 'and', { get: function () { return this; } });
            Object.defineProperty(Assertion.prototype, 'have', { get: function () { return this; } });
            Object.defineProperty(Assertion.prototype, 'has', { get: function () { return this; } });
            Object.defineProperty(Assertion.prototype, 'with', { get: function () { return this; } });
            Object.defineProperty(Assertion.prototype, 'a', { get: function () { return this; } });
            Object.defineProperty(Assertion.prototype, 'an', { get: function () { return this; } });
            Object.defineProperty(Assertion.prototype, 'not', { get: function () { this._neg = !this._neg; return this; } });
            Object.defineProperty(Assertion.prototype, 'ok', { get: function () { check(this, !!this._a, 'expected value to be truthy', 'expected value to be falsy'); return this; } });
            Object.defineProperty(Assertion.prototype, 'true', { get: function () { check(this, this._a === true, 'expected true', 'expected not true'); return this; } });
            Object.defineProperty(Assertion.prototype, 'false', { get: function () { check(this, this._a === false, 'expected false', 'expected not false'); return this; } });
            Object.defineProperty(Assertion.prototype, 'null', { get: function () { check(this, this._a === null, 'expected null', 'expected not null'); return this; } });
            Object.defineProperty(Assertion.prototype, 'undefined', { get: function () { check(this, this._a === undefined, 'expected undefined', 'expected defined'); return this; } });
            Object.defineProperty(Assertion.prototype, 'exist', { get: function () { check(this, this._a != null, 'expected value to exist', 'expected value not to exist'); return this; } });
            Object.defineProperty(Assertion.prototype, 'empty', { get: function () { check(this, _.isEmpty(this._a), 'expected empty', 'expected not empty'); return this; } });
            Assertion.prototype.equal = Assertion.prototype.equals = Assertion.prototype.eq = function (v) {
                check(this, this._a === v, 'expected ' + this._a + ' to equal ' + v, 'expected ' + this._a + ' not to equal ' + v); return this;
            };
            Assertion.prototype.eql = Assertion.prototype.eqls = function (v) {
                check(this, deepEqual(this._a, v), 'expected deep equality', 'expected not deep equal'); return this;
            };
            Assertion.prototype.above = Assertion.prototype.greaterThan = function (n) { check(this, this._a > n, 'expected ' + this._a + ' > ' + n, 'expected not >'); return this; };
            Assertion.prototype.below = Assertion.prototype.lessThan = function (n) { check(this, this._a < n, 'expected ' + this._a + ' < ' + n, 'expected not <'); return this; };
            Assertion.prototype.least = function (n) { check(this, this._a >= n, 'expected >= ' + n, 'expected not >='); return this; };
            Assertion.prototype.most = function (n) { check(this, this._a <= n, 'expected <= ' + n, 'expected not <='); return this; };
            Assertion.prototype.within = function (lo, hi) { check(this, this._a >= lo && this._a <= hi, 'expected within range', 'expected outside range'); return this; };
            Assertion.prototype.include = Assertion.prototype.includes = Assertion.prototype.contain = Assertion.prototype.contains = function (v) {
                var ok = typeof this._a === 'string' ? this._a.indexOf(v) >= 0 : _.includes(this._a, v);
                check(this, ok, 'expected to include ' + v, 'expected not to include ' + v); return this;
            };
            Assertion.prototype.match = function (re) { check(this, re.test(this._a), 'expected to match', 'expected not to match'); return this; };
            Assertion.prototype.property = function (name, val) {
                var has = this._a != null && Object.prototype.hasOwnProperty.call(this._a, name);
                check(this, has, 'expected property ' + name, 'expected no property ' + name);
                if (val !== undefined) check(this, this._a[name] === val, 'expected property ' + name + ' to equal ' + val, '');
                return new Assertion(this._a ? this._a[name] : undefined, false);
            };
            Assertion.prototype.lengthOf = function (n) { check(this, this._a != null && this._a.length === n, 'expected length ' + n, 'expected length not ' + n); return this; };
            Object.defineProperty(Assertion.prototype, 'length', { get: function () { var self = this; var f = function (n) { return self.lengthOf(n); }; return f; } });
            Assertion.prototype.throw = Assertion.prototype.throws = function () {
                var threw = false; try { this._a(); } catch (e) { threw = true; }
                check(this, threw, 'expected function to throw', 'expected function not to throw'); return this;
            };
            Assertion.prototype.a = Assertion.prototype.an = function (type) {
                var actual = Array.isArray(this._a) ? 'array' : (this._a === null ? 'null' : typeof this._a);
                check(this, actual === type, 'expected type ' + type + ' but got ' + actual, 'expected type not ' + type); return this;
            };
            function expect(actual) { return new Assertion(actual, false); }
            var assert = function (value, msg) { if (!value) fail(msg || 'assertion failed'); };
            assert.equal = function (a, b, m) { if (a != b) fail(m || (a + ' != ' + b)); };
            assert.strictEqual = function (a, b, m) { if (a !== b) fail(m || (a + ' !== ' + b)); };
            assert.deepEqual = function (a, b, m) { if (!deepEqual(a, b)) fail(m || 'not deep equal'); };
            assert.notEqual = function (a, b, m) { if (a == b) fail(m || 'expected not equal'); };
            assert.isTrue = function (a, m) { if (a !== true) fail(m || 'expected true'); };
            assert.isFalse = function (a, m) { if (a !== false) fail(m || 'expected false'); };
            assert.isNull = function (a, m) { if (a !== null) fail(m || 'expected null'); };
            assert.isNotNull = function (a, m) { if (a === null) fail(m || 'expected not null'); };
            assert.isOk = function (a, m) { if (!a) fail(m || 'expected truthy'); };
            assert.fail = function (m) { fail(m || 'fail'); };
            return { expect: expect, assert: assert };
        };

        // ----- tv4 (JSON schema, draft-04 subset) -----
        __modules['tv4'] = function () {
            return { validate: function (data, schema) { return __validateSchema(data, schema).length === 0; },
                     validateResult: function (data, schema) {
                         var errs = __validateSchema(data, schema);
                         return { valid: errs.length === 0, error: errs.length ? { message: errs[0] } : null };
                     },
                     validateMultiple: function (data, schema) {
                         var errs = __validateSchema(data, schema);
                         return { valid: errs.length === 0, errors: errs.map(function (m) { return { message: m }; }) };
                     } };
        };

        // ----- ajv (thin wrapper over the same validator) -----
        __modules['ajv'] = function () {
            function Ajv() {}
            Ajv.prototype.compile = function (schema) {
                var validate = function (data) {
                    var errs = __validateSchema(data, schema);
                    validate.errors = errs.length ? errs.map(function (m) { return { message: m }; }) : null;
                    return errs.length === 0;
                };
                return validate;
            };
            Ajv.prototype.validate = function (schema, data) {
                var errs = __validateSchema(data, schema);
                this.errors = errs.length ? errs.map(function (m) { return { message: m }; }) : null;
                return errs.length === 0;
            };
            return Ajv;
        };

        // Shared minimal JSON-schema validator (type/required/properties/items/enum).
        function __validateSchema(data, schema, path) {
            path = path || '';
            var errs = [];
            if (!schema || typeof schema !== 'object') return errs;
            if (schema.type) {
                var t = schema.type, actual = Array.isArray(data) ? 'array' : (data === null ? 'null' : typeof data);
                if (actual === 'number' && data % 1 === 0) actual = 'integer';
                var types = Array.isArray(t) ? t : [t];
                var ok = types.some(function (x) { return x === actual || (x === 'number' && actual === 'integer'); });
                if (!ok) errs.push((path || 'value') + ' should be ' + t + ' but was ' + actual);
            }
            if (schema.enum && schema.enum.indexOf(data) < 0) errs.push((path || 'value') + ' not in enum');
            if (schema.required && data && typeof data === 'object') {
                schema.required.forEach(function (k) {
                    if (!Object.prototype.hasOwnProperty.call(data, k)) errs.push('missing required property ' + (path ? path + '.' : '') + k);
                });
            }
            if (schema.properties && data && typeof data === 'object') {
                for (var k in schema.properties) {
                    if (Object.prototype.hasOwnProperty.call(data, k))
                        errs = errs.concat(__validateSchema(data[k], schema.properties[k], (path ? path + '.' : '') + k));
                }
            }
            if (schema.items && Array.isArray(data)) {
                for (var i = 0; i < data.length; i++)
                    errs = errs.concat(__validateSchema(data[i], schema.items, (path || 'value') + '[' + i + ']'));
            }
            return errs;
        }

        // ----- csv-parse/lib/sync -----
        (function () {
            function parseCsv(input, options) {
                options = options || {};
                var text = String(input == null ? '' : input);
                var rows = [], row = [], field = '', i = 0, inQuotes = false;
                while (i < text.length) {
                    var c = text[i];
                    if (inQuotes) {
                        if (c === '"') { if (text[i + 1] === '"') { field += '"'; i += 2; continue; } inQuotes = false; i++; continue; }
                        field += c; i++; continue;
                    }
                    if (c === '"') { inQuotes = true; i++; continue; }
                    if (c === ',') { row.push(field); field = ''; i++; continue; }
                    if (c === '\r') { i++; continue; }
                    if (c === '\n') { row.push(field); rows.push(row); row = []; field = ''; i++; continue; }
                    field += c; i++;
                }
                if (field.length > 0 || row.length > 0) { row.push(field); rows.push(row); }
                if (!options.columns) return rows;
                var cols = options.columns === true ? rows.shift() : options.columns;
                return rows.map(function (r) {
                    var obj = {};
                    for (var j = 0; j < cols.length; j++) obj[cols[j]] = r[j];
                    return obj;
                });
            }
            __modules['csv-parse/lib/sync'] = function () { return parseCsv; };
            __modules['csv-parse/sync'] = function () { return { parse: parseCsv }; };
        })();

        // ----- cheerio (minimal: tag/class/id selectors, text/attr/each) -----
        __modules['cheerio'] = function () {
            function load(html) {
                var doc = xml2Json('<__root>' + String(html == null ? '' : html) + '</__root>');
                // cheerio's DOM is richer than we can offer under Jint; expose the parsed tree
                // and a small selector surface that covers the common "scrape a value" case.
                function collect(node, tag, acc) {
                    if (node == null || typeof node !== 'object') return;
                    for (var k in node) {
                        if (k === '$' || k === '_') continue;
                        var arr = node[k];
                        if (!Array.isArray(arr)) arr = [arr];
                        for (var i = 0; i < arr.length; i++) {
                            if (k === tag) acc.push(arr[i]);
                            collect(arr[i], tag, acc);
                        }
                    }
                }
                function textOf(node) {
                    if (node == null) return '';
                    if (typeof node !== 'object') return String(node);
                    var t = node._ != null ? String(node._) : '';
                    for (var k in node) {
                        if (k === '$' || k === '_') continue;
                        var arr = Array.isArray(node[k]) ? node[k] : [node[k]];
                        for (var i = 0; i < arr.length; i++) t += textOf(arr[i]);
                    }
                    return t;
                }
                var $ = function (selector) {
                    var tag = selector.replace(/^[.#]/, '');
                    var matches = [];
                    collect(doc.__root, tag, matches);
                    return {
                        length: matches.length,
                        text: function () { return matches.map(textOf).join(''); },
                        attr: function (name) { var m = matches[0]; return m && m.$ ? m.$[name] : undefined; },
                        first: function () { return { text: function () { return matches[0] ? textOf(matches[0]) : ''; } }; },
                        each: function (fn) { for (var i = 0; i < matches.length; i++) fn(i, matches[i]); return this; }
                    };
                };
                $.text = function () { return textOf(doc.__root); };
                return $;
            }
            return { load: load };
        };

        // ----- Node core: querystring -----
        __modules['querystring'] = function () {
            return {
                parse: function (str) {
                    var out = {};
                    if (!str) return out;
                    String(str).split('&').forEach(function (pair) {
                        if (!pair) return;
                        var idx = pair.indexOf('=');
                        var k = idx < 0 ? pair : pair.slice(0, idx);
                        var v = idx < 0 ? '' : pair.slice(idx + 1);
                        k = decodeURIComponent(k.replace(/\+/g, ' '));
                        v = decodeURIComponent(v.replace(/\+/g, ' '));
                        if (Object.prototype.hasOwnProperty.call(out, k)) {
                            if (!Array.isArray(out[k])) out[k] = [out[k]];
                            out[k].push(v);
                        } else out[k] = v;
                    });
                    return out;
                },
                stringify: function (obj) {
                    if (!obj) return '';
                    var parts = [];
                    Object.keys(obj).forEach(function (k) {
                        var v = obj[k];
                        var vals = Array.isArray(v) ? v : [v];
                        vals.forEach(function (val) {
                            parts.push(encodeURIComponent(k) + '=' + encodeURIComponent(val == null ? '' : val));
                        });
                    });
                    return parts.join('&');
                },
                escape: encodeURIComponent,
                unescape: decodeURIComponent
            };
        };

        // ----- Node core: url -----
        __modules['url'] = function () {
            var qs = require('querystring');
            function parse(urlStr, parseQuery) {
                var m = /^([a-zA-Z][a-zA-Z0-9+.-]*:)?(\/\/([^/?#]*))?([^?#]*)(\?[^#]*)?(#.*)?$/.exec(String(urlStr)) || [];
                var host = m[3] || '';
                var atIdx = host.indexOf('@');
                var auth = atIdx >= 0 ? host.slice(0, atIdx) : null;
                var hostname = atIdx >= 0 ? host.slice(atIdx + 1) : host;
                var portIdx = hostname.lastIndexOf(':');
                var port = portIdx >= 0 ? hostname.slice(portIdx + 1) : null;
                if (portIdx >= 0) hostname = hostname.slice(0, portIdx);
                var search = m[5] || '';
                return {
                    protocol: m[1] || null, slashes: !!m[2], auth: auth, host: host, hostname: hostname, port: port,
                    pathname: m[4] || '', search: search, query: parseQuery ? qs.parse(search.replace(/^\?/, '')) : search.replace(/^\?/, ''),
                    hash: m[6] || null, href: String(urlStr)
                };
            }
            return { parse: parse, URL: typeof URL !== 'undefined' ? URL : undefined, URLSearchParams: typeof URLSearchParams !== 'undefined' ? URLSearchParams : undefined };
        };

        // ----- Node core: path (posix) -----
        __modules['path'] = function () {
            function basename(p, ext) {
                var b = String(p).replace(/\/+$/, '').split('/').pop() || '';
                if (ext && b.slice(-ext.length) === ext) b = b.slice(0, -ext.length);
                return b;
            }
            return {
                sep: '/',
                basename: basename,
                dirname: function (p) { var s = String(p).replace(/\/+$/, '').split('/'); s.pop(); return s.join('/') || '.'; },
                extname: function (p) { var b = basename(p); var i = b.lastIndexOf('.'); return i > 0 ? b.slice(i) : ''; },
                join: function () { return Array.prototype.slice.call(arguments).filter(Boolean).join('/').replace(/\/+/g, '/'); },
                normalize: function (p) { return String(p).replace(/\/+/g, '/'); },
                parse: function (p) {
                    var base = basename(p); var i = base.lastIndexOf('.');
                    return { root: '', dir: this.dirname(p), base: base, ext: i > 0 ? base.slice(i) : '', name: i > 0 ? base.slice(0, i) : base };
                }
            };
        };

        // ----- Node core: util -----
        __modules['util'] = function () {
            function format(fmt) {
                var args = Array.prototype.slice.call(arguments, 1), i = 0;
                if (typeof fmt !== 'string') return [fmt].concat(args).map(inspect).join(' ');
                var out = fmt.replace(/%[sdjifoO%]/g, function (t) {
                    if (t === '%%') return '%';
                    if (i >= args.length) return t;
                    var a = args[i++];
                    if (t === '%d' || t === '%i') return String(parseInt(a, 10));
                    if (t === '%f') return String(parseFloat(a));
                    if (t === '%j') return JSON.stringify(a);
                    if (t === '%s') return String(a);
                    return inspect(a);
                });
                for (; i < args.length; i++) out += ' ' + inspect(args[i]);
                return out;
            }
            function inspect(o) { try { return typeof o === 'string' ? o : JSON.stringify(o); } catch (e) { return String(o); } }
            return {
                format: format, inspect: inspect,
                isArray: Array.isArray,
                isDate: function (v) { return v instanceof Date; },
                isRegExp: function (v) { return v instanceof RegExp; },
                types: { isDate: function (v) { return v instanceof Date; } }
            };
        };

        // ----- Node core: assert -----
        __modules['assert'] = function () {
            function ok(v, m) { if (!v) throw new Error(m || 'assertion failed'); }
            ok.ok = ok;
            ok.equal = function (a, b, m) { if (a != b) throw new Error(m || (a + ' != ' + b)); };
            ok.strictEqual = function (a, b, m) { if (a !== b) throw new Error(m || (a + ' !== ' + b)); };
            ok.notEqual = function (a, b, m) { if (a == b) throw new Error(m || 'expected not equal'); };
            ok.deepEqual = function (a, b, m) { if (!_.isEqual(a, b)) throw new Error(m || 'not deep equal'); };
            ok.deepStrictEqual = ok.deepEqual;
            ok.fail = function (m) { throw new Error(m || 'failed'); };
            ok.throws = function (fn, m) { var t = false; try { fn(); } catch (e) { t = true; } if (!t) throw new Error(m || 'expected throw'); };
            return ok;
        };

        // ----- Node core: buffer -----
        __modules['buffer'] = function () {
            function Buffer(hex) { this.__hex = hex; }
            Buffer.from = function (data, enc) {
                if (data && data.__hex !== undefined) return new Buffer(data.__hex);
                var s = String(data == null ? '' : data);
                enc = (enc || 'utf8').toLowerCase();
                if (enc === 'base64') return new Buffer(__vegha.base64ToHex(s));
                if (enc === 'hex') return new Buffer(s);
                if (enc === 'binary' || enc === 'latin1') return new Buffer(__vegha.utf8ToHex(__vegha.atob(__vegha.btoa(s))));
                return new Buffer(__vegha.utf8ToHex(s));
            };
            Buffer.prototype.toString = function (enc) {
                enc = (enc || 'utf8').toLowerCase();
                if (enc === 'base64') return __vegha.hexToBase64(this.__hex);
                if (enc === 'hex') return this.__hex;
                return __vegha.hexToUtf8(this.__hex);
            };
            Buffer.isBuffer = function (v) { return v instanceof Buffer; };
            return { Buffer: Buffer };
        };
        // Node exposes Buffer as a global too.
        var Buffer = require('buffer').Buffer;

        // ----- Node core: events (EventEmitter) -----
        __modules['events'] = function () {
            function EventEmitter() { this._ev = {}; }
            EventEmitter.prototype.on = EventEmitter.prototype.addListener = function (e, fn) { (this._ev[e] = this._ev[e] || []).push(fn); return this; };
            EventEmitter.prototype.once = function (e, fn) { var self = this; function g() { self.removeListener(e, g); fn.apply(this, arguments); } return this.on(e, g); };
            EventEmitter.prototype.removeListener = EventEmitter.prototype.off = function (e, fn) {
                var l = this._ev[e]; if (l) { var i = l.indexOf(fn); if (i >= 0) l.splice(i, 1); } return this;
            };
            EventEmitter.prototype.removeAllListeners = function (e) { if (e) delete this._ev[e]; else this._ev = {}; return this; };
            EventEmitter.prototype.emit = function (e) {
                var l = this._ev[e]; if (!l || !l.length) return false;
                var args = Array.prototype.slice.call(arguments, 1);
                l.slice().forEach(function (fn) { fn.apply(this, args); });
                return true;
            };
            EventEmitter.prototype.listeners = function (e) { return (this._ev[e] || []).slice(); };
            return { EventEmitter: EventEmitter };
        };

        // ----- Explicitly unsupported heavy modules: fail loud, not cryptic -----
        (function () {
            var unsupported = ['postman-collection', 'stream', 'zlib', 'timers', 'string_decoder', 'punycode', 'net', 'tls', 'http', 'https', 'fs', 'os', 'child_process'];
            unsupported.forEach(function (name) {
                __modules[name] = function () {
                    throw new Error("Module '" + name + "' is not available in the Vegha sandbox.");
                };
            });
        })();
        """;
}
