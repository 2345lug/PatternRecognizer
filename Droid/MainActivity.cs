using Android.App;
using Android.Content.PM;
using Android.OS;


namespace CustomRenderer.Droid
{
    [Activity (Label = "CustomRenderer.Droid", Icon = "@drawable/icon", Theme = "@style/MainTheme", MainLauncher = true, ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation)]
	public class MainActivity : global::Xamarin.Forms.Platform.Android.FormsAppCompatActivity
	{
		protected override void OnCreate (Bundle bundle)
		{
			base.OnCreate (bundle);
			Rg.Plugins.Popup.Popup.Init(this);
			global::Xamarin.Forms.Forms.Init (this, bundle);
			LoadApplication (new App ());
			
		}

		public override void OnBackPressed()
		{
			Rg.Plugins.Popup.Popup.SendBackPressed(base.OnBackPressed);
		}
	}
}

