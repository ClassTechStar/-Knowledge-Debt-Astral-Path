# Keep WebView / JS interface if added later
-keepclassmembers class * {
    @android.webkit.JavascriptInterface <methods>;
}
