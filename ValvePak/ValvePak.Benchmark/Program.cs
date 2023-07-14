using BenchmarkDotNet.Running;
using BenchmarkDotNet.Attributes;
using SteamDatabase.ValvePak;

namespace ValvePak.Benchmark;

public class Program
{
	public static void Main()
	{
		BenchmarkRunner.Run(typeof(Program).Assembly);
	}

	[MemoryDiagnoser(true)]
	public class Vpk
	{
		readonly Package package = new();
		readonly Package packageSorted = new();

		[GlobalSetup]
		public void Setup()
		{
			package.Read("P:\\Steam\\steamapps\\common\\dota 2 beta\\game\\dota\\pak01_dir.vpk");

			packageSorted.OptimizeEntriesForBinarySearch();
			packageSorted.Read("P:\\Steam\\steamapps\\common\\dota 2 beta\\game\\dota\\pak01_dir.vpk");
		}

		[Benchmark]
		public void Last()
		{
			package.FindEntry("particles/ui_mouseactions/range_finder_direction_background.vtex_c");
		}

		[Benchmark]
		public void LastSorted()
		{
			packageSorted.FindEntry("particles/ui_mouseactions/range_finder_direction_background.vtex_c");
		}

		/*
		[Benchmark]
		public void Middle()
		{
			package.FindEntry("materials/models/items/witchdoctor/aghsbp_2021_witch_doctor_weapon/aghsbp_2021_witch_doctor_weapon_metalnessmask_psd_8ebea870.vtex_c");
		}

		[Benchmark]
		public void MiddleSorted()
		{
			packageSorted.FindEntry("materials/models/items/witchdoctor/aghsbp_2021_witch_doctor_weapon/aghsbp_2021_witch_doctor_weapon_metalnessmask_psd_8ebea870.vtex_c");
		}

		[Benchmark]
		public void First()
		{
			package.FindEntry("dev/point_worldtext_default_vmat_g_tcolor_f2d94a6b.vtex_c");
		}

		[Benchmark]
		public void FirstSorted()
		{
			packageSorted.FindEntry("dev/point_worldtext_default_vmat_g_tcolor_f2d94a6b.vtex_c");
		}
		*/
	}
}
