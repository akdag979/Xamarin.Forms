using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using Foundation;
using ObjCRuntime;
using UIKit;
using WebKit;
using Xamarin.Forms.Internals;
using PreserveAttribute = Foundation.PreserveAttribute;
using Uri = System.Uri;

namespace Xamarin.Forms.Platform.iOS
{
	public class WkWebViewRenderer : WKWebView, IVisualElementRenderer, IWebViewDelegate, IEffectControlProvider, ITabStop
	{
		EventTracker _events;
		bool _ignoreSourceChanges;
		WebNavigationEvent _lastBackForwardEvent;
		VisualElementPackager _packager;
#pragma warning disable 0414
		VisualElementTracker _tracker;
#pragma warning restore 0414


		[Preserve(Conditional = true)]
		public WkWebViewRenderer() : base(RectangleF.Empty, new WKWebViewConfiguration())
		{
		}


		[Preserve(Conditional = true)]
		public WkWebViewRenderer(WKWebViewConfiguration config) : base(RectangleF.Empty, config)
		{
		}

		WebView WebView => Element as WebView;

		public VisualElement Element { get; private set; }

		public event EventHandler<VisualElementChangedEventArgs> ElementChanged;

		public SizeRequest GetDesiredSize(double widthConstraint, double heightConstraint)
		{
			return NativeView.GetSizeRequest(widthConstraint, heightConstraint, 44, 44);
		}

		public void SetElement(VisualElement element)
		{
			var oldElement = Element;
			Element = element;
			Element.PropertyChanged += HandlePropertyChanged;
			WebView.EvalRequested += OnEvalRequested;
			WebView.EvaluateJavaScriptRequested += OnEvaluateJavaScriptRequested;
			WebView.GoBackRequested += OnGoBackRequested;
			WebView.GoForwardRequested += OnGoForwardRequested;
			WebView.ReloadRequested += OnReloadRequested;
			NavigationDelegate = new CustomWebViewNavigationDelegate(this);
			UIDelegate = new CustomWebViewUIDelegate();

			BackgroundColor = UIColor.Clear;

			AutosizesSubviews = true;

			_tracker = new VisualElementTracker(this);

			_packager = new VisualElementPackager(this);
			_packager.Load();

			_events = new EventTracker(this);
			_events.LoadEvents(this);

			Load();

			OnElementChanged(new VisualElementChangedEventArgs(oldElement, element));

			EffectUtilities.RegisterEffectControlProvider(this, oldElement, element);

			if (Element != null && !string.IsNullOrEmpty(Element.AutomationId))
				AccessibilityIdentifier = Element.AutomationId;

			if (element != null)
				element.SendViewInitialized(this);
		}

		public void SetElementSize(Size size)
		{
			Layout.LayoutChildIntoBoundingRegion(Element, new Rectangle(Element.X, Element.Y, size.Width, size.Height));
		}

		public void LoadHtml(string html, string baseUrl)
		{
			if (html != null)
				LoadHtmlString(html, baseUrl == null ? new NSUrl(NSBundle.MainBundle.BundlePath, true) : new NSUrl(baseUrl, true));
		}

		public async void LoadUrl(string url)
		{
			try
			{
				var uri = new Uri(url);
				var safeHostUri = new Uri($"{uri.Scheme}://{uri.Authority}", UriKind.Absolute);
				var safeRelativeUri = new Uri($"{uri.PathAndQuery}{uri.Fragment}", UriKind.Relative);
				NSUrlRequest request = new NSUrlRequest(new Uri(safeHostUri, safeRelativeUri));

				await SyncNativeCookies(url);
				LoadRequest(request);
			}
			catch (Exception exc)
			{
				Log.Warning(nameof(WkWebViewRenderer), $"Unable to Load Url {exc}");
			}
		}

		public override void LayoutSubviews()
		{
			base.LayoutSubviews();

			// ensure that inner scrollview properly resizes when frame of webview updated
			ScrollView.Frame = Bounds;
		}

		protected override void Dispose(bool disposing)
		{
			if (disposing)
			{
				if (IsLoading)
					StopLoading();

				Element.PropertyChanged -= HandlePropertyChanged;
				WebView.EvalRequested -= OnEvalRequested;
				WebView.EvaluateJavaScriptRequested -= OnEvaluateJavaScriptRequested;
				WebView.GoBackRequested -= OnGoBackRequested;
				WebView.GoForwardRequested -= OnGoForwardRequested;
				WebView.ReloadRequested -= OnReloadRequested;

				_tracker?.Dispose();
				_packager?.Dispose();
			}

			base.Dispose(disposing);
		}

		protected virtual void OnElementChanged(VisualElementChangedEventArgs e)
		{
			var changed = ElementChanged;
			if (changed != null)
				changed(this, e);
		}

		HashSet<string> _loadedCookies = new HashSet<string>();

		async Task<List<NSHttpCookie>> GetCookiesFromNativeStore(string url)
		{
			NSHttpCookie[] _initialCookiesLoaded = null;
			if (Forms.IsiOS11OrNewer)
			{
				_initialCookiesLoaded = await Configuration.WebsiteDataStore.HttpCookieStore.GetAllCookiesAsync();
			}
			else
			{
				// I haven't found a different way to get the cookies pre ios 11
				var cookieString = await WebView.EvaluateJavaScriptAsync("document.cookie");

				if (cookieString != null)
				{
					CookieContainer extractCookies = new CookieContainer();
					var uri = new Uri(url);

					foreach (var cookie in cookieString.Split(';'))
						extractCookies.SetCookies(uri, cookie);

					var extracted = extractCookies.GetCookies(uri);
					_initialCookiesLoaded = new NSHttpCookie[extracted.Count];
					for(int i = 0; i < extracted.Count; i++)
					{
						_initialCookiesLoaded[i] = new NSHttpCookie(extracted[i]);
					}
				}
			}

			_initialCookiesLoaded = _initialCookiesLoaded ?? new NSHttpCookie[0];

			List<NSHttpCookie> existingCookies = new List<NSHttpCookie>();
			string domain = new Uri(url).Host;
			foreach (var cookie in _initialCookiesLoaded)
			{
				// we don't care that much about this being accurate
				// the cookie container will split the cookies up more correctly
				if (!cookie.Domain.Contains(domain) && !domain.Contains(cookie.Domain))
					continue;

				existingCookies.Add(cookie);
			}

			return existingCookies;
		}

		async Task InitialCookiePreloadIfNecessary(string url)
		{
			var myCookieJar = WebView.Cookies;
			if (myCookieJar == null)
				return;

			var uri = new Uri(url);

			if (!_loadedCookies.Add(uri.Host))
				return;

			// pre ios 11 we sync cookies after navigated
			if (!Forms.IsiOS11OrNewer)
				return;

			var cookies = myCookieJar.GetCookies(uri);
			var existingCookies = await GetCookiesFromNativeStore(url);
			foreach (var nscookie in existingCookies)
			{
				if (cookies[nscookie.Name] == null)
				{
					string cookieH = $"{nscookie.Name}={nscookie.Value}; domain={nscookie.Domain}; path={nscookie.Path}";
					myCookieJar.SetCookies(uri, cookieH);
				}
			}
		}

		internal async Task SyncNativeCookiesToElement(string url)
		{
			if (String.IsNullOrWhiteSpace(url))
				return;

			var myCookieJar = WebView.Cookies;
			if (myCookieJar == null)
				return;

			var uri = new Uri(url);
			var cookies = myCookieJar.GetCookies(uri);
			var retrieveCurrentWebCookies = await GetCookiesFromNativeStore(url);

			foreach (var nscookie in retrieveCurrentWebCookies)
			{
				if (cookies[nscookie.Name] == null)
				{
					string cookieH = $"{nscookie.Name}={nscookie.Value}; domain={nscookie.Domain}; path={nscookie.Path}";

					myCookieJar.SetCookies(uri, cookieH);
				}
			}

			foreach (Cookie cookie in cookies)
			{
				NSHttpCookie nSHttpCookie = null;

				foreach(var findCookie in retrieveCurrentWebCookies)
				{
					if(findCookie.Name == cookie.Name)
					{
						nSHttpCookie = findCookie;
						break;
					}
				}

				if (nSHttpCookie == null)
					cookie.Expired = true;
				else
					cookie.Value = nSHttpCookie.Value;
			}

			await SyncNativeCookies(url);
		}

		async Task SyncNativeCookies(string url)
		{
			if (String.IsNullOrWhiteSpace(url))
				return;

			var uri = new Uri(url);
			var myCookieJar = WebView.Cookies;
			if (myCookieJar == null)
				return;

			await InitialCookiePreloadIfNecessary(url);
			var cookies = myCookieJar.GetCookies(uri);
			if (cookies == null)
				return;

			var retrieveCurrentWebCookies = await GetCookiesFromNativeStore(url);

			List<NSHttpCookie> deleteCookies = new List<NSHttpCookie>();
			foreach (var cookie in retrieveCurrentWebCookies)
			{
				if (cookies[cookie.Name] != null)
					continue;

				deleteCookies.Add(cookie);
			}

			List<Cookie> cookiesToSet = new List<Cookie>();
			foreach (Cookie cookie in cookies)
			{
				bool changeCookie = true;

				// This code is used to only push updates to cookies that have changed.
				// This doesn't quite work on on iOS 10 if we have to delete any cookies.
				// I haven't found a way on iOS 10 to remove individual cookies. 
				// The trick we use on Android with writing a cookie that expires doesn't work
				// So on iOS10 if the user wants to remove any cookies we just delete 
				// the cookie for the entire domain inside of DeleteCookies and then rewrite
				// all the cookies
				if (Forms.IsiOS11OrNewer || deleteCookies.Count == 0)
				{
					foreach (var nsCookie in retrieveCurrentWebCookies)
					{
						// if the cookie value hasn't changed don't set it again
						if (nsCookie.Domain == cookie.Domain &&
							nsCookie.Name == cookie.Name &&
							nsCookie.Value == cookie.Value)
						{
							changeCookie = false;
							break;
						}
					}
				}

				if (changeCookie)
					cookiesToSet.Add(cookie);
			}

			await SetCookie(cookiesToSet);
			await DeleteCookies(deleteCookies);
		}

		async Task SetCookie(List<Cookie> cookies)
		{
			if (Forms.IsiOS11OrNewer)
			{
				foreach(var cookie in cookies)
					await Configuration.WebsiteDataStore.HttpCookieStore.SetCookieAsync(new NSHttpCookie(cookie));
			}
			else
			{
				Configuration.UserContentController.RemoveAllUserScripts();

				if (cookies.Count > 0)
				{
					WKUserScript wKUserScript = new WKUserScript(new NSString(GetCookieString(cookies)), WKUserScriptInjectionTime.AtDocumentStart, false);

					Configuration.UserContentController.AddUserScript(wKUserScript);
				}
			}
		}

		async Task DeleteCookies(List<NSHttpCookie> cookies)
		{
			if (Forms.IsiOS11OrNewer)
			{
				foreach (var cookie in cookies)
					await Configuration.WebsiteDataStore.HttpCookieStore.DeleteCookieAsync(cookie);
			}
			else
			{
				var wKWebsiteDataStore = WKWebsiteDataStore.DefaultDataStore;

				// This is the only way I've found to delete cookies on pre ios 11
				// I tried to set an expired cookie but it doesn't delete the cookie
				// So, just deleting the whole domain is the best option I've found
				WKWebsiteDataStore.DefaultDataStore.FetchDataRecordsOfTypes(WKWebsiteDataStore.AllWebsiteDataTypes, (NSArray records) =>
				{
					for (nuint i = 0; i < records.Count; i++)
					{
						var record = records.GetItem<WKWebsiteDataRecord>(i);

						foreach(var deleteme in cookies)
						{
							if (record.DisplayName.Contains(deleteme.Domain) || deleteme.Domain.Contains(record.DisplayName))
							{
								WKWebsiteDataStore.DefaultDataStore.RemoveDataOfTypes(record.DataTypes,
									  new[] { record }, () => { });

								break;
							}

						}
					}
				});
			}
		}

		void HandlePropertyChanged(object sender, PropertyChangedEventArgs e)
		{
			if (e.PropertyName == WebView.SourceProperty.PropertyName)
				Load();
		}

		void Load()
		{
			if (_ignoreSourceChanges)
				return;

			if (((WebView)Element).Source != null)
				((WebView)Element).Source.Load(this);

			UpdateCanGoBackForward();
		}

		void OnEvalRequested(object sender, EvalRequested eventArg)
		{
			EvaluateJavaScriptAsync(eventArg.Script);
		}

		async Task<string> OnEvaluateJavaScriptRequested(string script)
		{
			var result = await EvaluateJavaScriptAsync(script);
			return result?.ToString();
		}

		void OnGoBackRequested(object sender, EventArgs eventArgs)
		{
			if (CanGoBack)
			{
				_lastBackForwardEvent = WebNavigationEvent.Back;
				GoBack();
			}

			UpdateCanGoBackForward();
		}

		void OnGoForwardRequested(object sender, EventArgs eventArgs)
		{
			if (CanGoForward)
			{
				_lastBackForwardEvent = WebNavigationEvent.Forward;
				GoForward();
			}

			UpdateCanGoBackForward();
		}

		async void OnReloadRequested(object sender, EventArgs eventArgs)
		{
			try
			{
			
				await SyncNativeCookies(Url?.AbsoluteUrl?.ToString());
			}
			catch (Exception exc)
			{
				Log.Warning(nameof(WkWebViewRenderer), $"Syncing Existing Cookies Failed: {exc}");
			}

			Reload();
		}

		void UpdateCanGoBackForward()
		{
			((IWebViewController)WebView).CanGoBack = CanGoBack;
			((IWebViewController)WebView).CanGoForward = CanGoForward;
		}

		string GetCookieString(List<Cookie> existingCookies)
		{
			StringBuilder cookieBuilder = new StringBuilder();
			foreach (System.Net.Cookie jCookie in existingCookies)
			{
				cookieBuilder.Append("document.cookie = '");
				cookieBuilder.Append(jCookie.Name);
				cookieBuilder.Append("=");

				if (jCookie.Expired)
				{
					cookieBuilder.Append($"; Max-Age=0");
					cookieBuilder.Append($"; expires=Sun, 31 Dec 2000 00:00:00 UTC");
				}
				else
				{
					cookieBuilder.Append(jCookie.Value);
					cookieBuilder.Append($"; Max-Age={jCookie.Expires.Subtract(DateTime.UtcNow).TotalSeconds}");
				}

				if (!String.IsNullOrWhiteSpace(jCookie.Domain))
				{
					cookieBuilder.Append($"; Domain={jCookie.Domain}");
				}
				if (!String.IsNullOrWhiteSpace(jCookie.Domain))
				{
					cookieBuilder.Append($"; Path={jCookie.Path}");
				}
				if (jCookie.Secure)
				{
					cookieBuilder.Append($"; Secure");
				}
				if (jCookie.HttpOnly)
				{
					cookieBuilder.Append($"; HttpOnly");
				}

				cookieBuilder.Append("';");
			}

			return cookieBuilder.ToString();
		}

		class CustomWebViewNavigationDelegate : WKNavigationDelegate
		{
			readonly WkWebViewRenderer _renderer;
			WebNavigationEvent _lastEvent;

			public CustomWebViewNavigationDelegate(WkWebViewRenderer renderer)
			{
				if (renderer == null)
					throw new ArgumentNullException("renderer");
				_renderer = renderer;
			}

			WebView WebView => _renderer.WebView;

			public override void DidFailNavigation(WKWebView webView, WKNavigation navigation, NSError error)
			{
				var url = GetCurrentUrl();
				WebView.SendNavigated(
					new WebNavigatedEventArgs(_lastEvent, new UrlWebViewSource { Url = url }, url, WebNavigationResult.Failure)
				);

				_renderer.UpdateCanGoBackForward();
			}

			public override void DidFinishNavigation(WKWebView webView, WKNavigation navigation)
			{
				if (webView.IsLoading)
					return;

				var url = GetCurrentUrl();
				if (url == $"file://{NSBundle.MainBundle.BundlePath}/")
					return;

				_renderer._ignoreSourceChanges = true;
				WebView.SetValueFromRenderer(WebView.SourceProperty, new UrlWebViewSource { Url = url });
				_renderer._ignoreSourceChanges = false;
				ProcessNavigated(url);
			}

			async void ProcessNavigated(string url)
			{
				try
				{
					if(_renderer?.WebView?.Cookies != null)
						await _renderer.SyncNativeCookiesToElement(url);
				}
				catch(Exception exc)
				{
					Log.Warning(nameof(WkWebViewRenderer), $"Failed to Sync Cookies {exc}");
				}

				var args = new WebNavigatedEventArgs(_lastEvent, WebView.Source, url, WebNavigationResult.Success);
				WebView.SendNavigated(args);
				_renderer.UpdateCanGoBackForward();

			}

			public override void DidStartProvisionalNavigation(WKWebView webView, WKNavigation navigation)
			{
			}

			// https://stackoverflow.com/questions/37509990/migrating-from-uiwebview-to-wkwebview
			public override void DecidePolicy(WKWebView webView, WKNavigationAction navigationAction, Action<WKNavigationActionPolicy> decisionHandler)
			{
				var navEvent = WebNavigationEvent.NewPage;
				var navigationType = navigationAction.NavigationType;
				switch (navigationType)
				{
					case WKNavigationType.LinkActivated:
						navEvent = WebNavigationEvent.NewPage;

						if (navigationAction.TargetFrame == null)
							webView?.LoadRequest(navigationAction.Request);

						break;
					case WKNavigationType.FormSubmitted:
						navEvent = WebNavigationEvent.NewPage;
						break;
					case WKNavigationType.BackForward:
						navEvent = _renderer._lastBackForwardEvent;
						break;
					case WKNavigationType.Reload:
						navEvent = WebNavigationEvent.Refresh;
						break;
					case WKNavigationType.FormResubmitted:
						navEvent = WebNavigationEvent.NewPage;
						break;
					case WKNavigationType.Other:
						navEvent = WebNavigationEvent.NewPage;
						break;
				}

				_lastEvent = navEvent;
				var request = navigationAction.Request;
				var lastUrl = request.Url.ToString();
				var args = new WebNavigatingEventArgs(navEvent, new UrlWebViewSource { Url = lastUrl }, lastUrl);

				WebView.SendNavigating(args);
				_renderer.UpdateCanGoBackForward();
				decisionHandler(args.Cancel ? WKNavigationActionPolicy.Cancel : WKNavigationActionPolicy.Allow);
			}

			string GetCurrentUrl()
			{
				return _renderer?.Url?.AbsoluteUrl?.ToString();
			}
		}

		class CustomWebViewUIDelegate : WKUIDelegate
		{
			static string LocalOK = NSBundle.FromIdentifier("com.apple.UIKit").GetLocalizedString("OK");
			static string LocalCancel = NSBundle.FromIdentifier("com.apple.UIKit").GetLocalizedString("Cancel");

			public override void RunJavaScriptAlertPanel(WKWebView webView, string message, WKFrameInfo frame, Action completionHandler)
			{
				PresentAlertController(
					webView,
					message,
					okAction: _ => completionHandler()
				);
			}

			public override void RunJavaScriptConfirmPanel(WKWebView webView, string message, WKFrameInfo frame, Action<bool> completionHandler)
			{
				PresentAlertController(
					webView,
					message,
					okAction: _ => completionHandler(true),
					cancelAction: _ => completionHandler(false)
				);
			}

			public override void RunJavaScriptTextInputPanel(
				WKWebView webView, string prompt, string defaultText, WKFrameInfo frame, Action<string> completionHandler)
			{
				PresentAlertController(
					webView,
					prompt,
					defaultText: defaultText,
					okAction: x => completionHandler(x.TextFields[0].Text),
					cancelAction: _ => completionHandler(null)
				);
			}

			static string GetJsAlertTitle(WKWebView webView)
			{
				// Emulate the behavior of UIWebView dialogs.
				// The scheme and host are used unless local html content is what the webview is displaying,
				// in which case the bundle file name is used.

				if (webView.Url != null && webView.Url.AbsoluteString != $"file://{NSBundle.MainBundle.BundlePath}/")
					return $"{webView.Url.Scheme}://{webView.Url.Host}";

				return new NSString(NSBundle.MainBundle.BundlePath).LastPathComponent;
			}

			static UIAlertAction AddOkAction(UIAlertController controller, Action handler)
			{
				var action = UIAlertAction.Create(LocalOK, UIAlertActionStyle.Default, (_) => handler());
				controller.AddAction(action);
				controller.PreferredAction = action;
				return action;
			}

			static UIAlertAction AddCancelAction(UIAlertController controller, Action handler)
			{
				var action = UIAlertAction.Create(LocalCancel, UIAlertActionStyle.Cancel, (_) => handler());
				controller.AddAction(action);
				return action;
			}

			static void PresentAlertController(
				WKWebView webView,
				string message,
				string defaultText = null,
				Action<UIAlertController> okAction = null,
				Action<UIAlertController> cancelAction = null)
			{
				var controller = UIAlertController.Create(GetJsAlertTitle(webView), message, UIAlertControllerStyle.Alert);

				if (defaultText != null)
					controller.AddTextField((textField) => textField.Text = defaultText);

				if (okAction != null)
					AddOkAction(controller, () => okAction(controller));

				if (cancelAction != null)
					AddCancelAction(controller, () => cancelAction(controller));

				GetTopViewController(UIApplication.SharedApplication.GetKeyWindow().RootViewController)
					.PresentViewController(controller, true, null);
			}

			static UIViewController GetTopViewController(UIViewController viewController)
			{
				if (viewController is UINavigationController navigationController)
					return GetTopViewController(navigationController.VisibleViewController);

				if (viewController is UITabBarController tabBarController)
					return GetTopViewController(tabBarController.SelectedViewController);

				if (viewController.PresentedViewController != null)
					return GetTopViewController(viewController.PresentedViewController);

				return viewController;
			}
		}

		#region IPlatformRenderer implementation

		public UIView NativeView
		{
			get { return this; }
		}

		public UIViewController ViewController
		{
			get { return null; }
		}

		UIView ITabStop.TabStop => this;

		#endregion

		void IEffectControlProvider.RegisterEffect(Effect effect)
		{
			VisualElementRenderer<VisualElement>.RegisterEffect(effect, this, NativeView);
		}
	}
}